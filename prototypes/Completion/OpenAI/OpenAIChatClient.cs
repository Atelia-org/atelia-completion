using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIChatClient : ICompletionClient {
    private const string DebugCategory = "Provider";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> ReservedRequestFieldNames = new(StringComparer.Ordinal) {
        "model",
        "messages",
        "n",
        "stream",
        "tools",
        "tool_choice",
        "parallel_tool_calls",
        "reasoning_effort",
        "thinking"
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly OpenAIChatDialect _dialect;
    private readonly OpenAIChatClientOptions _options;

    public string Name => _httpClient.BaseAddress?.Host ?? "openai";
    public string ApiSpecId => "openai-chat-v1";

    public OpenAIChatClient(
        string? apiKey,
        HttpClient httpClient,
        OpenAIChatDialect? dialect = null,
        OpenAIChatClientOptions? options = null
    ) {
        Atelia.Completion.ReasoningBlockCodecs.EnsureRegistered();

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = CompletionHttpRequestUtility.RequireConfiguredBaseAddress(_httpClient, nameof(OpenAIChatClient));

        _dialect = dialect ?? OpenAIChatDialects.Strict;
        options ??= new OpenAIChatClientOptions();
        if (!Enum.IsDefined(options.ReasoningEffort)) {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ReasoningEffort,
                "Unknown reasoning effort."
            );
        }
        _options = new OpenAIChatClientOptions {
            ReasoningEffort = options.ReasoningEffort,
            ExtraBody = options.ExtraBody is null
                ? null
                : (JsonObject)options.ExtraBody.DeepClone()
        };

        DebugUtil.Info(
            DebugCategory,
            $"[OpenAI] Client initialized base={_httpClient.BaseAddress}, dialect={_dialect.Name}, extraBodyKeys={_options.ExtraBody?.Count ?? 0}, reasoningEffort={_options.ReasoningEffort}"
        );
    }

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        DebugUtil.Info(DebugCategory, $"[OpenAI] Starting call model={request.ModelId}");

        var invocation = CompletionDescriptor.From(this, request);
        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(
            request,
            _dialect,
            invocation
        );
        using var response = await SendStreamingRequestAsync(apiRequest, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var aggregator = new CompletionAggregator(invocation, observer);
        var parser = new OpenAIChatStreamParser(_dialect.WhitespaceContentMode, _dialect.ReasoningMode);
        var stoppedEarly = false;

        try {
            await foreach (var frame in CompletionSseEventReader.ReadFramesAsync(stream, cancellationToken)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Data is null) { continue; }

                if (string.Equals(frame.Data, "[DONE]", StringComparison.Ordinal)) {
                    CompletionStreamTermination.RequireTerminalEvent(
                        parser.TerminalEventObserved,
                        "OpenAI chat/completions"
                    );
                    break;
                }

                parser.ParseEvent(frame.Data, aggregator);
                if (parser.TerminalEventObserved) { break; }

                if (aggregator.ShouldStop) {
                    stoppedEarly = true;
                    break;
                }
            }

            if (stoppedEarly) {
                parser.DiscardIncompleteStreamingState();
                aggregator.AbortIncompleteStreamingState();
                aggregator.MarkIncomplete(detail: "Streaming observer stopped OpenAI chat completion early.");
            }
            else {
                CompletionStreamTermination.RequireTerminalEvent(
                    parser.TerminalEventObserved,
                    "OpenAI chat/completions"
                );
            }
        }
        catch (Exception exception) {
            CleanupAfterFailure(parser, aggregator, exception);
            throw;
        }

        DebugUtil.Trace(DebugCategory, "[OpenAI] Stream completed");
        return aggregator.Build();
    }

    /// <summary>
    /// The current OpenAI Chat surfaces do not expose a uniform request-level
    /// cache-lifetime control, so validated provider-neutral hints are accepted
    /// as an explicit no-op.
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

    private async Task<HttpResponseMessage> SendStreamingRequestAsync(OpenAIChatApiRequest apiRequest, CancellationToken cancellationToken) {
        return await CompletionHttpRequestUtility.SendStreamingRequestAsync(
            _httpClient,
            CreateHttpRequest(apiRequest),
            "OpenAI chat/completions request",
            cancellationToken
        );
    }

    private HttpRequestMessage CreateHttpRequest(OpenAIChatApiRequest apiRequest) {
        apiRequest.ExtensionData = BuildRequestExtensionData(apiRequest);
        var json = JsonSerializer.Serialize(apiRequest, SerializerOptions);
        DebugUtil.Trace(DebugCategory, $"[OpenAI] Request payload length={json.Length}, dialect={_dialect.Name}");

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(_apiKey)) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private Dictionary<string, JsonElement>? BuildRequestExtensionData(
        OpenAIChatApiRequest apiRequest
    ) {
        Dictionary<string, JsonElement>? extensionData = BuildExtraBodyExtensionData();
        ApplyReasoningControl(apiRequest, ref extensionData);
        return extensionData;
    }

    private Dictionary<string, JsonElement>? BuildExtraBodyExtensionData() {
        if (_options.ExtraBody is null || _options.ExtraBody.Count == 0) { return null; }

        var extensionData = new Dictionary<string, JsonElement>(_options.ExtraBody.Count, StringComparer.Ordinal);

        foreach (var (propertyName, propertyValue) in _options.ExtraBody) {
            if (ReservedRequestFieldNames.Contains(propertyName)) {
                throw new InvalidOperationException(
                    $"OpenAI extra body field '{propertyName}' collides with a reserved request property."
                );
            }

            extensionData[propertyName] = propertyValue is null
                ? JsonSerializer.SerializeToElement((object?)null, SerializerOptions)
                : propertyValue.Deserialize<JsonElement>(SerializerOptions);
        }

        return extensionData;
    }

    private void ApplyReasoningControl(
        OpenAIChatApiRequest apiRequest,
        ref Dictionary<string, JsonElement>? extensionData
    ) {
        CompletionReasoningEffort effort = _options.ReasoningEffort;
        if (effort is CompletionReasoningEffort.ProviderDefault) { return; }

        switch (_dialect.ReasoningControlMode) {
            case OpenAIChatReasoningControlMode.OpenAIReasoningEffort:
                apiRequest.ReasoningEffort = MapOpenAIReasoningEffort(effort);
                return;

            case OpenAIChatReasoningControlMode.DeepSeekV4ReasoningEffort:
                extensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (extensionData.ContainsKey("thinking")) {
                    throw new InvalidOperationException(
                        "OpenAI extra body field 'thinking' conflicts with the configured reasoning effort."
                    );
                }
                extensionData["thinking"] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, string> {
                        ["type"] = effort is CompletionReasoningEffort.Disabled
                            ? "disabled"
                            : "enabled"
                    },
                    SerializerOptions
                );
                if (effort is not CompletionReasoningEffort.Disabled) {
                    apiRequest.ReasoningEffort = effort switch {
                        CompletionReasoningEffort.Max => "max",
                        CompletionReasoningEffort.Low or
                        CompletionReasoningEffort.Medium or
                        CompletionReasoningEffort.High => "high",
                        _ => throw UnknownReasoningEffort(effort)
                    };
                }
                return;

            case OpenAIChatReasoningControlMode.QwenThinkingSwitch:
                extensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (extensionData.ContainsKey("chat_template_kwargs")) {
                    throw new InvalidOperationException(
                        "OpenAI extra body field 'chat_template_kwargs' conflicts with the configured reasoning effort."
                    );
                }
                extensionData["chat_template_kwargs"] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, bool> {
                        ["enable_thinking"] = effort is not CompletionReasoningEffort.Disabled
                    },
                    SerializerOptions
                );
                return;

            case OpenAIChatReasoningControlMode.Unsupported:
            default:
                throw new InvalidOperationException(
                    $"OpenAI chat dialect '{_dialect.Name}' does not define an explicit reasoning control mapping."
                );
        }
    }

    private static string MapOpenAIReasoningEffort(CompletionReasoningEffort effort)
        => effort switch {
            CompletionReasoningEffort.Disabled => "none",
            CompletionReasoningEffort.Low => "low",
            CompletionReasoningEffort.Medium => "medium",
            CompletionReasoningEffort.High => "high",
            CompletionReasoningEffort.Max => "xhigh",
            _ => throw UnknownReasoningEffort(effort)
        };

    private static ArgumentOutOfRangeException UnknownReasoningEffort(
        CompletionReasoningEffort effort
    ) => new(nameof(effort), effort, "Unknown reasoning effort.");

    private static void CleanupAfterFailure(
        OpenAIChatStreamParser parser,
        CompletionAggregator aggregator,
        Exception originalException
    ) {
        try {
            parser.DiscardIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[OpenAI] Parser cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }

        try {
            aggregator.AbortIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[OpenAI] Observer cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }
    }
}
