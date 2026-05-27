using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIResponsesClient : ICompletionClient {
    private const string DebugCategory = "Provider";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> ReservedRequestFieldNames = new(StringComparer.Ordinal) {
        "model",
        "instructions",
        "input",
        "tools",
        "stream",
        "store",
        "include",
        "parallel_tool_calls"
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly OpenAIResponsesClientOptions _options;

    public string Name => _httpClient.BaseAddress?.Host ?? "openai";
    public string ApiSpecId => "openai-responses-v1";

    public OpenAIResponsesClient(
        string? apiKey,
        HttpClient httpClient,
        OpenAIResponsesClientOptions? options = null
    ) {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = CompletionHttpRequestUtility.RequireConfiguredBaseAddress(_httpClient, nameof(OpenAIResponsesClient));
        _options = options ?? new OpenAIResponsesClientOptions();

        DebugUtil.Info(
            DebugCategory,
            $"[OpenAI/Responses] Client initialized base={_httpClient.BaseAddress}, extraBodyKeys={_options.ExtraBody?.Count ?? 0}"
        );
    }

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        DebugUtil.Info(DebugCategory, $"[OpenAI/Responses] Starting call model={request.ModelId}");

        var apiRequest = BuildApiRequest(request);
        using var response = await SendStreamingRequestAsync(apiRequest, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var invocation = CompletionDescriptor.From(this, request);
        var aggregator = new CompletionAggregator(invocation, observer);
        var parser = new OpenAIResponsesStreamParser();
        string? line;
        var stoppedEarly = false;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null) {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line)) { continue; }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) { continue; }

            var json = line["data:".Length..].TrimStart();
            if (json == "[DONE]") { break; }

            parser.ParseEvent(json, aggregator);
            if (aggregator.ShouldStop) {
                stoppedEarly = true;
                break;
            }
        }

        if (stoppedEarly) {
            parser.DiscardIncompleteStreamingState();
            aggregator.AbortIncompleteStreamingState();
        }
        else {
            parser.Complete(aggregator);
        }

        DebugUtil.Trace(DebugCategory, "[OpenAI/Responses] Stream completed");
        return aggregator.Build();
    }

    private async Task<HttpResponseMessage> SendStreamingRequestAsync(
        OpenAIResponsesApiRequest apiRequest,
        CancellationToken cancellationToken
    ) {
        return await CompletionHttpRequestUtility.SendStreamingRequestAsync(
            _httpClient,
            CreateHttpRequest(apiRequest),
            "OpenAI responses request",
            cancellationToken
        );
    }

    private HttpRequestMessage CreateHttpRequest(OpenAIResponsesApiRequest apiRequest) {
        apiRequest.ExtensionData = BuildExtraBodyExtensionData();
        var json = JsonSerializer.Serialize(apiRequest, SerializerOptions);
        DebugUtil.Trace(DebugCategory, $"[OpenAI/Responses] Request payload length={json.Length}");

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses") {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };

        if (!string.IsNullOrWhiteSpace(_apiKey)) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private Dictionary<string, JsonElement>? BuildExtraBodyExtensionData() {
        if (_options.ExtraBody is null || _options.ExtraBody.Count == 0) { return null; }

        var extensionData = new Dictionary<string, JsonElement>(_options.ExtraBody.Count, StringComparer.Ordinal);
        foreach (var (propertyName, propertyValue) in _options.ExtraBody) {
            if (ReservedRequestFieldNames.Contains(propertyName)) {
                throw new InvalidOperationException(
                    $"OpenAI Responses extra body field '{propertyName}' collides with a reserved request property."
                );
            }

            extensionData[propertyName] = propertyValue is null
                ? JsonSerializer.SerializeToElement((object?)null, SerializerOptions)
                : propertyValue.Deserialize<JsonElement>(SerializerOptions);
        }

        return extensionData;
    }

    private OpenAIResponsesApiRequest BuildApiRequest(CompletionRequest request) {
        var state = new ProjectionState();
        var input = new List<OpenAIResponsesInputItem>();

        foreach (var contextMessage in request.Context) {
            switch (contextMessage) {
                case ToolResultsMessage toolResults:
                    BuildToolResultItems(toolResults, input, state);
                    break;

                case ObservationMessage observation:
                    BuildObservationItem(observation, input, state);
                    break;

                case ActionMessage action:
                    BuildActionItems(action, input, state);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported history message '{contextMessage.GetType().Name}' for OpenAI Responses projection."
                    );
            }
        }

        EnsureNoPendingToolCalls(state, "context ended");

        return new OpenAIResponsesApiRequest {
            Model = request.ModelId,
            Instructions = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt,
            Input = input,
            Tools = BuildToolDefinitions(request.Tools),
            Stream = true,
            Store = _options.Store,
            Include = _options.IncludeEncryptedReasoning ? ["reasoning.encrypted_content"] : null,
            ParallelToolCalls = _options.ParallelToolCalls
        };
    }

    private static void BuildObservationItem(
        ObservationMessage observation,
        List<OpenAIResponsesInputItem> input,
        ProjectionState state
    ) {
        EnsureNoPendingToolCalls(state, $"observation before tool results content={observation.Content}");
        if (string.IsNullOrWhiteSpace(observation.Content)) { return; }

        input.Add(CreateMessageItem("user", "input_text", observation.Content));
    }

    private static void BuildActionItems(ActionMessage action, List<OpenAIResponsesInputItem> input, ProjectionState state) {
        EnsureNoPendingToolCalls(state, $"assistant action before tool results blockCount={action.Blocks.Count}");

        var textBuilder = new StringBuilder();
        var pendingToolCalls = new List<PendingToolCall>();

        foreach (var block in action.Blocks) {
            switch (block) {
                case ActionBlock.Text textBlock:
                    textBuilder.Append(textBlock.Content);
                    break;

                case ActionBlock.ToolCall toolCallBlock:
                    FlushAssistantTextIfNeeded(textBuilder, input);

                    var call = toolCallBlock.Call;
                    var toolCallId = string.IsNullOrWhiteSpace(call.ToolCallId)
                        ? CreateSyntheticToolCallId(call.ToolName, pendingToolCalls.Count)
                        : call.ToolCallId;

                    input.Add(
                        new OpenAIResponsesFunctionCallItem {
                            CallId = toolCallId,
                            Name = call.ToolName,
                            Arguments = StreamParserToolUtility.NormalizeRawArgumentsJson(call.RawArgumentsJson)
                        }
                    );

                    pendingToolCalls.Add(new PendingToolCall(toolCallId, call.ToolName));
                    break;

                case OpenAIResponsesReasoningBlock reasoningBlock:
                    throw new InvalidOperationException(
                        $"OpenAI Responses inline request projection does not yet replay reasoning block '{reasoningBlock.GetType().Name}'."
                    );

                case ActionBlock.ReasoningBlock:
                    throw new InvalidOperationException(
                        "OpenAI Responses replay only supports OpenAIResponsesReasoningBlock. Cross-provider reasoning replay is not supported."
                    );

                default:
                    throw new InvalidOperationException(
                        $"Unsupported action block kind '{block.Kind}' for OpenAI Responses projection."
                    );
            }
        }

        FlushAssistantTextIfNeeded(textBuilder, input);
        state.SetPendingToolCalls(pendingToolCalls);
    }

    private static void BuildToolResultItems(
        ToolResultsMessage toolResults,
        List<OpenAIResponsesInputItem> input,
        ProjectionState state
    ) {
        if (state.PendingToolCalls.Count > 0) {
            var resultsByCallId = CreateResultLookup(toolResults.Results);

            foreach (var pendingToolCall in state.PendingToolCalls) {
                if (resultsByCallId.Remove(pendingToolCall.ToolCallId, out var result)) {
                    EnsureMatchingToolName(result, pendingToolCall);
                    input.Add(
                        new OpenAIResponsesFunctionCallOutputItem {
                            CallId = result.ToolCallId,
                            Output = result.GetFlattenedText()
                        }
                    );
                    continue;
                }

                throw new InvalidOperationException(
                    $"Tool results are missing for pending call_id='{pendingToolCall.ToolCallId}'. ToolResultsMessage.Results must align 1:1 with the pending function_call items."
                );
            }

            if (resultsByCallId.Count > 0) {
                var unexpectedCallId = resultsByCallId.Keys.First();
                throw new InvalidOperationException(
                    $"Tool result call_id='{unexpectedCallId}' does not match the pending function_call items."
                );
            }

            state.ClearPendingToolCalls();
        }
        else if (toolResults.Results.Count > 0) {
            throw new InvalidOperationException("Tool results appeared without a preceding function_call item.");
        }

        if (!string.IsNullOrWhiteSpace(toolResults.Content)) {
            input.Add(CreateMessageItem("user", "input_text", toolResults.Content));
        }
    }

    private static Dictionary<string, ToolResult> CreateResultLookup(IReadOnlyList<ToolResult> results) {
        var lookup = new Dictionary<string, ToolResult>(StringComparer.Ordinal);

        foreach (var result in results) {
            if (string.IsNullOrWhiteSpace(result.ToolCallId)) {
                throw new InvalidOperationException("Tool result is missing tool_call_id.");
            }

            if (!lookup.TryAdd(result.ToolCallId, result)) {
                throw new InvalidOperationException($"Duplicate tool result tool_call_id='{result.ToolCallId}'.");
            }
        }

        return lookup;
    }

    private static void EnsureMatchingToolName(ToolResult result, PendingToolCall pendingToolCall) {
        if (string.Equals(result.ToolName, pendingToolCall.ToolName, StringComparison.Ordinal)) { return; }

        throw new InvalidOperationException(
            $"OpenAI Responses tool result name mismatch for call_id='{pendingToolCall.ToolCallId}': expected '{pendingToolCall.ToolName}', got '{result.ToolName}'."
        );
    }

    private static void FlushAssistantTextIfNeeded(StringBuilder textBuilder, List<OpenAIResponsesInputItem> input) {
        if (textBuilder.Length == 0) { return; }

        input.Add(CreateMessageItem("assistant", "output_text", textBuilder.ToString()));
        textBuilder.Clear();
    }

    private static OpenAIResponsesMessageItem CreateMessageItem(string role, string contentType, string text) {
        return new OpenAIResponsesMessageItem {
            Role = role,
            Content = [
                CreateContentItem(contentType, text)
            ]
        };
    }

    private static OpenAIResponsesContentItem CreateContentItem(string contentType, string text) {
        return contentType switch {
            "input_text" => new OpenAIResponsesInputTextContentItem { Text = text },
            "output_text" => new OpenAIResponsesOutputTextContentItem { Text = text },
            _ => throw new InvalidOperationException($"Unsupported OpenAI Responses content type '{contentType}'.")
        };
    }

    private static List<OpenAIResponsesTool>? BuildToolDefinitions(ImmutableArray<ToolDefinition> tools) {
        if (tools.IsDefaultOrEmpty) { return null; }

        var list = new List<OpenAIResponsesTool>(tools.Length);
        foreach (var definition in tools) {
            list.Add(
                new OpenAIResponsesTool {
                    Name = definition.Name,
                    Description = definition.Description,
                    Parameters = JsonToolSchemaBuilder.BuildSchema(definition),
                    Strict = true
                }
            );
        }

        return list;
    }

    private static string CreateSyntheticToolCallId(string toolName, int index) {
        var safeName = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName;
        return $"openai-responses-call-{safeName}-{index}";
    }

    private static void EnsureNoPendingToolCalls(ProjectionState state, string nextContextDescription) {
        if (state.PendingToolCalls.Count == 0) { return; }

        var pendingIds = string.Join(", ", state.PendingToolCalls.Select(static call => call.ToolCallId));
        throw new InvalidOperationException(
            $"Pending function_call items must be followed immediately by tool results before {nextContextDescription}. pending=[{pendingIds}]"
        );
    }

    private sealed class ProjectionState {
        private List<PendingToolCall> _pendingToolCalls = new();

        public IReadOnlyList<PendingToolCall> PendingToolCalls => _pendingToolCalls;

        public void ClearPendingToolCalls() {
            _pendingToolCalls.Clear();
        }

        public void SetPendingToolCalls(List<PendingToolCall> pendingToolCalls) {
            _pendingToolCalls = pendingToolCalls.Count > 0 ? pendingToolCalls : new List<PendingToolCall>();
        }
    }

    private sealed record PendingToolCall(string ToolCallId, string ToolName);
}
