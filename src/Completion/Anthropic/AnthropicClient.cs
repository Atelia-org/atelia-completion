using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atelia.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;

namespace Atelia.Completion.Anthropic;

/// <summary>
/// Anthropic Messages API 客户端实现。
/// 规范：https://docs.anthropic.com/claude/reference/messages_post
/// </summary>
public sealed class AnthropicClient : ICompletionClient {
    private const string DebugCategory = "Provider";
    private const string DefaultApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly string _apiVersion;
    private readonly ProviderModelMaximumCache _modelMaximums;
    private readonly bool _enablePromptCaching;
    private readonly CompletionReasoningEffort _reasoningEffort;
    private readonly AnthropicPromptCacheTtl _promptCacheTtl;

    public string Name => _httpClient.BaseAddress?.Host ?? "anthropic";
    public string ApiSpecId => "messages-v1";

    public AnthropicClient(
        string? apiKey,
        HttpClient httpClient,
        string? apiVersion = null,
        bool enablePromptCaching = true,
        CompletionReasoningEffort reasoningEffort = CompletionReasoningEffort.ProviderDefault,
        AnthropicPromptCacheTtl promptCacheTtl = AnthropicPromptCacheTtl.ProviderDefault
    ) {
        Atelia.Completion.ReasoningBlockCodecs.EnsureRegistered();

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = CompletionHttpRequestUtility.RequireConfiguredBaseAddress(_httpClient, nameof(AnthropicClient));

        _apiVersion = string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion;
        _modelMaximums = new ProviderModelMaximumCache(
            FetchModelMaximumAsync
        );
        _enablePromptCaching = enablePromptCaching;
        _reasoningEffort = Enum.IsDefined(reasoningEffort)
            ? reasoningEffort
            : throw new ArgumentOutOfRangeException(nameof(reasoningEffort), reasoningEffort, "Unknown reasoning effort.");
        _promptCacheTtl = Enum.IsDefined(promptCacheTtl)
            ? promptCacheTtl
            : throw new ArgumentOutOfRangeException(
                nameof(promptCacheTtl),
                promptCacheTtl,
                "Unknown Anthropic prompt cache TTL."
            );

        DebugUtil.Info(DebugCategory, $"[Anthropic] Client initialized base={_httpClient.BaseAddress}, version={_apiVersion}, promptCaching={_enablePromptCaching}, promptCacheTtl={_promptCacheTtl}, reasoningEffort={_reasoningEffort}");
    }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => StreamCompletionCoreAsync(
        request,
        observer,
        _enablePromptCaching,
        _promptCacheTtl,
        CompletionInvocationOptions.Default,
        cancellationToken
    );

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(invocationOptions);
        invocationOptions.Validate();

        if (!_enablePromptCaching) {
            return StreamCompletionCoreAsync(
                request,
                observer,
                enablePromptCaching: false,
                _promptCacheTtl,
                invocationOptions,
                cancellationToken
            );
        }

        (bool enablePromptCaching, AnthropicPromptCacheTtl promptCacheTtl) =
            invocationOptions.PromptCacheReuseHint switch {
                PromptCacheReuseHint.ConnectionDefault => (true, _promptCacheTtl),
                PromptCacheReuseHint.NoReuseExpected => (false, _promptCacheTtl),
                PromptCacheReuseHint.ReuseExpectedSoon => (true, AnthropicPromptCacheTtl.FiveMinutes),
                PromptCacheReuseHint.ReuseExpectedAfterPause => (true, AnthropicPromptCacheTtl.OneHour),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(invocationOptions),
                    invocationOptions.PromptCacheReuseHint,
                    "Unknown prompt cache reuse hint."
                )
            };

        return StreamCompletionCoreAsync(
            request,
            observer,
            enablePromptCaching,
            promptCacheTtl,
            invocationOptions,
            cancellationToken
        );
    }

    private async Task<CompletionResult> StreamCompletionCoreAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        bool enablePromptCaching,
        AnthropicPromptCacheTtl promptCacheTtl,
        CompletionInvocationOptions invocationOptions,
        CancellationToken cancellationToken
    ) {
        DebugUtil.Info(DebugCategory, $"[Anthropic] Starting call model={request.ModelId}");

        var invocation = CompletionDescriptor.From(this, request);
        int modelMaximumTokens = await _modelMaximums.GetAsync(
            request.ModelId,
            cancellationToken
        ).ConfigureAwait(false);
        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(
            request,
            modelMaximumTokens,
            enablePromptCaching,
            _reasoningEffort,
            promptCacheTtl,
            invocation
        );
        using var response = await SendStreamingRequestAsync(apiRequest, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var aggregator = new CompletionAggregator(invocation, observer);
        aggregator.MergeUsage(
            PromptCacheTelemetryContext.Create(
                invocationOptions.PromptCacheReuseHint,
                _enablePromptCaching
                    ? PromptCacheSupportStatus.Supported
                    : PromptCacheSupportStatus.Unsupported,
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["explicitBreakpoint"] = enablePromptCaching
                        ? "true"
                        : "false",
                    ["ttl"] = promptCacheTtl switch {
                        AnthropicPromptCacheTtl.ProviderDefault =>
                            "provider-default",
                        AnthropicPromptCacheTtl.FiveMinutes => "5m",
                        AnthropicPromptCacheTtl.OneHour => "1h",
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(promptCacheTtl),
                            promptCacheTtl,
                            "Unknown Anthropic prompt cache TTL."
                        )
                    }
                }
            )
        );
        var parser = new AnthropicStreamParser();
        var eofDiagnostics = new CompletionSseEofDiagnostics();
        var committedFrameCount = 0;
        string? lastCommittedEventType = null;
        var stoppedEarly = false;
        var awaitingSseFrame = true;

        try {
            await foreach (var frame in CompletionSseEventReader.ReadFramesAsync(
                stream,
                cancellationToken,
                eofDiagnostics
            )) {
                awaitingSseFrame = false;
                cancellationToken.ThrowIfCancellationRequested();

                committedFrameCount++;
                lastCommittedEventType = frame.EventType;
                parser.ParseEvent(frame.Data, aggregator, frame.EventType);
                if (parser.TerminalEventObserved) { break; }

                if (aggregator.ShouldStop) {
                    stoppedEarly = true;
                    break;
                }

                awaitingSseFrame = true;
            }
            awaitingSseFrame = false;

            if (stoppedEarly) {
                parser.DiscardIncompleteStreamingState();
                aggregator.AbortIncompleteStreamingState();
                aggregator.MarkIncomplete(detail: "Streaming observer stopped Anthropic completion early.");
            }
            else if (!parser.TerminalEventObserved
                && eofDiagnostics.CleanEofObserved
                && !eofDiagnostics.HasPendingFrame
                && parser.TryFinalizeAtCleanEndOfStream(aggregator)) {
                DebugUtil.Warning(
                    DebugCategory,
                    "[Anthropic] Clean EOF omitted message_stop after an authoritative "
                        + "message_delta stop_reason; accepting the completed lifecycle. "
                        + BuildStreamDiagnosticContext(
                            response,
                            committedFrameCount,
                            lastCommittedEventType,
                            eofDiagnostics,
                            parser,
                            terminalSource: "clean-eof-stop-reason"
                        )
                );
            }
            else {
                CompletionStreamTermination.RequireTerminalEvent(
                    parser.TerminalEventObserved,
                    "Anthropic Messages",
                    BuildStreamDiagnosticContext(
                        response,
                        committedFrameCount,
                        lastCommittedEventType,
                        eofDiagnostics,
                        parser,
                        terminalSource: "none"
                    )
                );
            }
        }
        catch (Exception exception) {
            if (awaitingSseFrame
                && exception is IOException
                && exception is not CompletionStreamInterruptedException) {
                TryLogTransportReadFailure(
                    response,
                    committedFrameCount,
                    lastCommittedEventType,
                    eofDiagnostics,
                    parser,
                    exception
                );
            }
            CleanupAfterFailure(parser, aggregator, exception);
            throw;
        }

        DebugUtil.Trace(DebugCategory, "[Anthropic] Stream completed");
        return aggregator.Build();
    }

    private static void TryLogTransportReadFailure(
        HttpResponseMessage response,
        int committedFrameCount,
        string? lastCommittedEventType,
        CompletionSseEofDiagnostics eofDiagnostics,
        AnthropicStreamParser parser,
        Exception exception
    ) {
        try {
            DebugUtil.Warning(
                DebugCategory,
                "[Anthropic] Transport read failed before clean EOF. "
                    + $"exceptionType={exception.GetType().Name}, "
                    + BuildStreamDiagnosticContext(
                        response,
                        committedFrameCount,
                        lastCommittedEventType,
                        eofDiagnostics,
                        parser,
                        terminalSource: "read-exception"
                    )
            );
        }
        catch {
            // Diagnostic logging must never replace the original transport exception.
        }
    }

    private static string BuildStreamDiagnosticContext(
        HttpResponseMessage response,
        int committedFrameCount,
        string? lastCommittedEventType,
        CompletionSseEofDiagnostics eofDiagnostics,
        AnthropicStreamParser parser,
        string terminalSource
    ) {
        var requestId = GetSafeResponseHeader(response, "request-id")
            ?? GetSafeResponseHeader(response, "x-request-id")
            ?? "none";
        var pendingEvent = eofDiagnostics.HasPendingFrame
            ? SanitizeDiagnosticToken(eofDiagnostics.PendingEventType ?? "unnamed")
            : "none";
        var pendingDataCharacters = eofDiagnostics.PendingDataCharacterCount?.ToString()
            ?? "none";

        return $"terminalSource={terminalSource}, httpVersion={response.Version}, "
            + $"status={(int)response.StatusCode}, requestId={requestId}, "
            + $"committedFrames={committedFrameCount}, "
            + $"lastEvent={SanitizeDiagnosticToken(lastCommittedEventType ?? "none")}, "
            + $"cleanEof={eofDiagnostics.CleanEofObserved.ToString().ToLowerInvariant()}, "
            + $"pendingFrame={eofDiagnostics.HasPendingFrame.ToString().ToLowerInvariant()}, "
            + $"pendingEvent={pendingEvent}, pendingDataChars={pendingDataCharacters}, "
            + parser.DescribeInterruptionState();
    }

    private static string? GetSafeResponseHeader(
        HttpResponseMessage response,
        string name
    ) {
        if (!response.Headers.TryGetValues(name, out var values)) { return null; }

        var value = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(value)
            ? null
            : SanitizeDiagnosticToken(value);
    }

    private static string SanitizeDiagnosticToken(string value) {
        const int MaximumLength = 128;
        var sanitized = new string(
            value
                .Take(MaximumLength)
                .Select(static character => char.IsControl(character) ? '?' : character)
                .ToArray()
        );
        return value.Length > MaximumLength ? $"{sanitized}..." : sanitized;
    }

    private async Task<HttpResponseMessage> SendStreamingRequestAsync(AnthropicApiRequest apiRequest, CancellationToken cancellationToken) {
        return await CompletionHttpRequestUtility.SendStreamingRequestAsync(
            _httpClient,
            CreateHttpRequest(apiRequest),
            "Anthropic messages request",
            cancellationToken
        );
    }

    private async Task<int> FetchModelMaximumAsync(
        string modelId,
        CancellationToken cancellationToken
    ) {
        using HttpRequestMessage request = CreateModelInfoRequest(modelId);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        ).ConfigureAwait(false);
        using JsonDocument document = await ProviderModelCapabilityResponse
            .ReadJsonObjectAsync(
                response,
                "Anthropic",
                cancellationToken
            ).ConfigureAwait(false);
        return ProviderModelCapabilityResponse.RequirePositivePlainInt32(
            document.RootElement,
            "max_tokens",
            "Anthropic"
        );
    }

    private HttpRequestMessage CreateModelInfoRequest(string modelId) {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/models/{Uri.EscapeDataString(modelId)}"
        );
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
        ApplyAuthenticationHeaders(request);
        return request;
    }

    private HttpRequestMessage CreateHttpRequest(AnthropicApiRequest apiRequest) {
        var json = JsonSerializer.Serialize(apiRequest, SerializerOptions);
        DebugUtil.Trace(DebugCategory, $"[Anthropic] Request payload length={json.Length}");

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages") {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        ApplyAuthenticationHeaders(request);

        return request;
    }

    private void ApplyAuthenticationHeaders(HttpRequestMessage request) {
        if (!string.IsNullOrWhiteSpace(_apiKey)) {
            request.Headers.Add("x-api-key", _apiKey);
        }

        if (!string.IsNullOrWhiteSpace(_apiVersion)) {
            request.Headers.Add("anthropic-version", _apiVersion);
        }
    }

    private static void CleanupAfterFailure(
        AnthropicStreamParser parser,
        CompletionAggregator aggregator,
        Exception originalException
    ) {
        try {
            parser.DiscardIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[Anthropic] Parser cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }

        try {
            aggregator.AbortIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[Anthropic] Observer cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }
    }
}
