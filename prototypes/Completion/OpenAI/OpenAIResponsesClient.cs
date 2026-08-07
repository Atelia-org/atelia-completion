using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
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
        "parallel_tool_calls",
        "reasoning"
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
        Atelia.Completion.ReasoningBlockCodecs.EnsureRegistered();

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = CompletionHttpRequestUtility.RequireConfiguredBaseAddress(_httpClient, nameof(OpenAIResponsesClient));
        options ??= new OpenAIResponsesClientOptions();
        _options = new OpenAIResponsesClientOptions {
            ReasoningEffort = options.ReasoningEffort,
            Store = options.Store,
            IncludeEncryptedReasoning = options.IncludeEncryptedReasoning,
            ParallelToolCalls = options.ParallelToolCalls,
            ExtraBody = options.ExtraBody is null
                ? null
                : (JsonObject)options.ExtraBody.DeepClone()
        };
        if (!Enum.IsDefined(_options.ReasoningEffort)) {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.ReasoningEffort,
                "Unknown reasoning effort."
            );
        }

        DebugUtil.Info(
            DebugCategory,
            $"[OpenAI/Responses] Client initialized base={_httpClient.BaseAddress}, extraBodyKeys={_options.ExtraBody?.Count ?? 0}, reasoningEffort={_options.ReasoningEffort}"
        );
    }

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        DebugUtil.Info(DebugCategory, $"[OpenAI/Responses] Starting call model={request.ModelId}");

        var invocation = CompletionDescriptor.From(this, request);
        var apiRequest = OpenAIResponsesMessageConverter.ConvertToApiRequest(
            request,
            _options,
            invocation
        );
        using var response = await SendStreamingRequestAsync(apiRequest, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var aggregator = new CompletionAggregator(invocation, observer);
        var parser = new OpenAIResponsesStreamParser();
        var stoppedEarly = false;

        try {
            await foreach (var frame in CompletionSseEventReader.ReadFramesAsync(stream, cancellationToken)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Data is null) { continue; }

                if (string.Equals(frame.Data, "[DONE]", StringComparison.Ordinal)) {
                    CompletionStreamTermination.RequireTerminalEvent(
                        parser.TerminalEventObserved,
                        "OpenAI Responses"
                    );
                    break;
                }

                parser.ParseEvent(frame.Data, aggregator, frame.EventType);
                if (parser.TerminalEventObserved) { break; }

                if (aggregator.ShouldStop) {
                    stoppedEarly = true;
                    break;
                }
            }

            if (stoppedEarly) {
                parser.DiscardIncompleteStreamingState();
                aggregator.AbortIncompleteStreamingState();
                aggregator.MarkIncomplete(detail: "Streaming observer stopped OpenAI Responses completion early.");
            }
            else {
                CompletionStreamTermination.RequireTerminalEvent(
                    parser.TerminalEventObserved,
                    "OpenAI Responses"
                );
            }
        }
        catch (Exception exception) {
            CleanupAfterFailure(parser, aggregator, exception);
            throw;
        }

        DebugUtil.Trace(DebugCategory, "[OpenAI/Responses] Stream completed");
        return aggregator.Build();
    }

    /// <summary>
    /// The current OpenAI Responses integration has no provider-neutral mapping
    /// for these hints, so validated values are accepted as an explicit no-op.
    /// </summary>
    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(invocationOptions);
        invocationOptions.Validate();
        return StreamCompletionAsync(request, observer, cancellationToken);
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
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

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

    private static void CleanupAfterFailure(
        OpenAIResponsesStreamParser parser,
        CompletionAggregator aggregator,
        Exception originalException
    ) {
        try {
            parser.DiscardIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[OpenAI/Responses] Parser cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }

        try {
            aggregator.AbortIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[OpenAI/Responses] Observer cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }
    }
}
