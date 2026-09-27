using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.ModelSpecs;
using Atelia.Completion.Transport;
using Atelia.Diagnostics;

namespace Atelia.Completion.Gemini;

public sealed class GeminiClient : ICompletionClient {
    private static readonly JsonSerializerOptions SerializerOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly ProviderModelMaximumCache _modelMaximums;
    private readonly CompletionModelSpecCatalog _modelSpecs;
    private readonly CompletionReasoningEffort _reasoningEffort;

    public string Name => _httpClient.BaseAddress?.Host ?? "generativelanguage.googleapis.com";
    public string ApiSpecId => "google-gemini-generate-content-v1beta";

    public GeminiClient(string? apiKey, HttpClient httpClient, GeminiClientOptions? options = null) {
        Atelia.Completion.ReasoningBlockCodecs.EnsureRegistered();

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = CompletionHttpRequestUtility.RequireConfiguredBaseAddress(_httpClient, nameof(GeminiClient));
        _modelMaximums = new ProviderModelMaximumCache(
            FetchModelMaximumAsync
        );
        options ??= new GeminiClientOptions();
        _modelSpecs = options.ModelSpecs ?? BuiltinModelSpecs.GeminiGenerateContent;
        _reasoningEffort = Enum.IsDefined(options.ReasoningEffort)
            ? options.ReasoningEffort
            : throw new ArgumentOutOfRangeException(nameof(options), "Unknown reasoning effort.");

        DebugUtil.Debug(
            CompletionDebugCategories.Provider,
            "[Gemini] Client initialized provider=Google Gemini"
        );
    }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => StreamCompletionCoreAsync(
        request,
        CompletionInvocationOptions.Default,
        observer,
        cancellationToken
    );

    private async Task<CompletionResult> StreamCompletionCoreAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        CompletionToolInputValidation.RequireJsonTools(request);
        DebugUtil.Debug(
            CompletionDebugCategories.Provider,
            $"[Gemini] Starting call model={request.ModelId}"
        );

        CompletionModelSpec? spec = _modelSpecs.Lookup(request.ModelId);
        GeminiThinkingConfig? thinking = ResolveThinkingConfig(spec);
        ValidateToolChoice(request.PromptPrefix.OutputContract, spec);
        var invocation = CompletionDescriptor.From(this, request);
        var apiRequest = GeminiMessageConverter.ProjectRequest(
            request,
            invocation
        );
        int modelMaximumTokens = spec?.OutputTokenLimit ?? await _modelMaximums.GetAsync(
            request.ModelId,
            cancellationToken
        ).ConfigureAwait(false);
        apiRequest.GenerationConfig = new GeminiGenerationConfig {
            MaxOutputTokens = modelMaximumTokens,
            ThinkingConfig = thinking
        };
        using var response = await SendStreamingRequestAsync(request.ModelId, apiRequest, cancellationToken);

        await using var stream = await CompletionHttpRequestUtility.OpenStreamAsync(response.Content, cancellationToken);

        var aggregator = new CompletionAggregator(invocation, observer);
        aggregator.MergeUsage(
            PromptCacheTelemetryContext.Create(
                invocationOptions.PromptCacheReuseHint,
                PromptCacheSupportStatus.Unknown,
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["mapping"] = "implicit-best-effort",
                    ["explicitCache"] = "separate-resource-lifecycle"
                }
            )
        );
        var parser = new GeminiStreamParser();
        var stoppedEarly = false;

        try {
            await foreach (var frame in CompletionSseEventReader.ReadFramesAsync(stream, cancellationToken)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Data is null) { continue; }

                parser.ParseEvent(frame.Data, aggregator);
                if (parser.TerminalEventObserved) {
                    break;
                }
                if (!parser.TerminalEventObserved
                    && aggregator.ShouldStop) {
                    stoppedEarly = true;
                    break;
                }
            }

            if (stoppedEarly) {
                parser.DiscardIncompleteStreamingState();
                aggregator.AbortIncompleteStreamingState();
                aggregator.MarkIncomplete(detail: "Streaming observer stopped Gemini completion early.");
            }
            else {
                CompletionStreamTermination.RequireTerminalEvent(
                    parser.TerminalEventObserved,
                    "Gemini streamGenerateContent"
                );
            }
        }
        catch (Exception exception) {
            CleanupAfterFailure(parser, aggregator, exception);
            throw;
        }

        DebugUtil.Debug(
            CompletionDebugCategories.Provider,
            "[Gemini] Stream completed"
        );
        return aggregator.Build();
    }

    private GeminiThinkingConfig? ResolveThinkingConfig(CompletionModelSpec? spec) {
        ReasoningEffortMapping? mapping = ModelSpecReasoning.Resolve(_reasoningEffort, spec);
        if (mapping is not { } resolved) { return null; }
        if (spec?.ReasoningMapper is null) {
            throw new NotSupportedException(
                "Explicit Gemini reasoning effort requires a model specification with a thinking-level mapper."
            );
        }
        // Generate Content level control cannot express truly disabled thinking.
        // Hosts may map to minimal with enabled semantics on models supporting it.
        if (resolved.EffectiveEffort is CompletionReasoningEffort.Disabled
            || resolved.WireLevel is not ("minimal" or "low" or "medium" or "high"
                or "MINIMAL" or "LOW" or "MEDIUM" or "HIGH")) {
            throw new ArgumentException(
                "The Gemini reasoning mapper must return an enabled effort and a supported native thinking-level name.",
                nameof(spec)
            );
        }
        return new GeminiThinkingConfig { ThinkingLevel = resolved.WireLevel };
    }

    private static void ValidateToolChoice(CompletionOutputContract contract, CompletionModelSpec? spec) {
        if (spec?.ForcedToolChoiceSupported is false
            && contract.ToolChoice.Kind is CompletionToolChoiceKind.RequiredAny or CompletionToolChoiceKind.RequiredNamed) {
            throw new CompletionRequestRejectedException(new CompletionTermination(
                CompletionTerminationKind.Failed,
                "model_specs.incompatible_tool_choice",
                "The selected model specification does not support forced tool selection."
            ));
        }
    }

    /// <summary>
    /// Gemini implicit caching is provider-managed and its explicit cache is a
    /// separate resource lifecycle, so validated per-invocation hints are an
    /// explicit no-op on this client surface.
    /// </summary>
    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(invocationOptions);
        invocationOptions.Validate();
        return StreamCompletionCoreAsync(
            request,
            invocationOptions,
            observer,
            cancellationToken
        );
    }

    private async Task<HttpResponseMessage> SendStreamingRequestAsync(
        string modelId,
        GeminiGenerateContentRequest apiRequest,
        CancellationToken cancellationToken
    ) {
        return await CompletionHttpRequestUtility.SendStreamingRequestAsync(
            _httpClient,
            CreateHttpRequest(modelId, apiRequest),
            "Gemini streamGenerateContent request",
            cancellationToken
        );
    }

    private async Task<int> FetchModelMaximumAsync(
        string modelId,
        CancellationToken cancellationToken
    ) {
        using HttpRequestMessage request = CreateModelInfoRequest(modelId);
        using HttpResponseMessage response = await CompletionHttpRequestUtility.SendAsync(
            _httpClient, request,
            cancellationToken
        ).ConfigureAwait(false);
        using JsonDocument document = await ProviderModelCapabilityResponse
            .ReadJsonObjectAsync(
                response,
                "Gemini",
                cancellationToken
            ).ConfigureAwait(false);
        return ProviderModelCapabilityResponse.RequirePositivePlainInt32(
            document.RootElement,
            "outputTokenLimit",
            "Gemini"
        );
    }

    private HttpRequestMessage CreateModelInfoRequest(string modelId) {
        string relativeUri = $"v1beta/{NormalizeModelPath(modelId)}";
        var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
        ApplyApiKeyHeader(request);
        return request;
    }

    private HttpRequestMessage CreateHttpRequest(string modelId, GeminiGenerateContentRequest apiRequest) {
        var json = JsonSerializer.Serialize(apiRequest, SerializerOptions);
        DebugUtil.Debug(
            CompletionDebugCategories.Provider,
            $"[Gemini] Request payload length={json.Length}"
        );

        var modelPath = NormalizeModelPath(modelId);
        var relativeUri = $"v1beta/{modelPath}:streamGenerateContent?alt=sse";

        var request = new HttpRequestMessage(HttpMethod.Post, relativeUri) {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        ApplyApiKeyHeader(request);

        return request;
    }

    private void ApplyApiKeyHeader(HttpRequestMessage request) {
        if (_apiKey is not null) {
            request.Headers.Add("x-goog-api-key", _apiKey);
        }
    }

    private static string NormalizeModelPath(string modelId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var normalized = modelId.StartsWith("models/", StringComparison.Ordinal)
            ? modelId
            : $"models/{modelId}";

        if (!normalized.StartsWith("models/", StringComparison.Ordinal)) { return normalized; }

        var suffix = normalized["models/".Length..];
        return $"models/{Uri.EscapeDataString(suffix)}";
    }

    private static void CleanupAfterFailure(
        GeminiStreamParser parser,
        CompletionAggregator aggregator,
        Exception originalException
    ) {
        try {
            parser.DiscardIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                CompletionDebugCategories.Provider,
                $"[Gemini] Parser cleanup failed while preserving {originalException.GetType().FullName}; "
                    + $"cleanupExceptionType={cleanupException.GetType().FullName}."
            );
        }

        try {
            aggregator.AbortIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                CompletionDebugCategories.Provider,
                $"[Gemini] Observer cleanup failed while preserving {originalException.GetType().FullName}; "
                    + $"cleanupExceptionType={cleanupException.GetType().FullName}."
            );
        }
    }
}
