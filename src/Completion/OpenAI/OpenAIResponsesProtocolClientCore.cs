using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

/// <summary>
/// Shared Responses projection and SSE-consumption core. Endpoint, credentials,
/// request identity and non-success HTTP semantics remain owned by the concrete
/// public or ChatGPT Codex client.
/// </summary>
internal sealed class OpenAIResponsesProtocolClientCore {
    private const string DebugCategory = "Provider";

    private readonly OpenAIResponsesClientOptions _requestOptions;
    private readonly string _apiSpecId;
    private readonly string _providerLabel;
    private readonly string _streamDisplayName;
    private readonly bool _sanitizeProviderErrors;
    private readonly OpenAIResponsesReasoningMapper _mapReasoningEffort;
    private readonly bool _supportsNativeRequiredNamedToolChoice;

    public OpenAIResponsesProtocolClientCore(
        OpenAIResponsesClientOptions requestOptions,
        string apiSpecId,
        string providerLabel,
        string streamDisplayName,
        OpenAIResponsesReasoningMapper mapReasoningEffort,
        bool supportsNativeRequiredNamedToolChoice,
        bool sanitizeProviderErrors
    ) {
        _requestOptions = requestOptions
            ?? throw new ArgumentNullException(nameof(requestOptions));
        _apiSpecId = string.IsNullOrWhiteSpace(apiSpecId)
            ? throw new ArgumentException(
                "API specification id must not be blank.",
                nameof(apiSpecId)
            )
            : apiSpecId;
        _providerLabel = string.IsNullOrWhiteSpace(providerLabel)
            ? throw new ArgumentException(
                "Provider label must not be blank.",
                nameof(providerLabel)
            )
            : providerLabel;
        _streamDisplayName = string.IsNullOrWhiteSpace(streamDisplayName)
            ? throw new ArgumentException(
                "Stream display name must not be blank.",
                nameof(streamDisplayName)
            )
            : streamDisplayName;
        _mapReasoningEffort = mapReasoningEffort
            ?? throw new ArgumentNullException(nameof(mapReasoningEffort));
        _supportsNativeRequiredNamedToolChoice =
            supportsNativeRequiredNamedToolChoice;
        _sanitizeProviderErrors = sanitizeProviderErrors;
    }

    public async Task<CompletionResult> StreamCompletionAsync(
        ICompletionClient owner,
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        Func<OpenAIResponsesApiRequest, CancellationToken,
            Task<HttpResponseMessage>> sendRequestAsync,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocationOptions);
        ArgumentNullException.ThrowIfNull(sendRequestAsync);
        invocationOptions.Validate();
        if (!string.Equals(owner.ApiSpecId, _apiSpecId, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                "Responses protocol owner and profile API specification ids do not match."
            );
        }

        DebugUtil.Info(
            DebugCategory,
            $"[{_providerLabel}] Starting call model={request.ModelId}"
        );

        CompletionDescriptor invocation = CompletionDescriptor.From(
            owner,
            request
        );
        OpenAIResponsesApiRequest apiRequest =
            OpenAIResponsesMessageConverter.ConvertToApiRequest(
                request,
                _requestOptions,
                invocation,
                _apiSpecId,
                _mapReasoningEffort,
                _supportsNativeRequiredNamedToolChoice
            );
        using HttpResponseMessage response = await sendRequestAsync(
            apiRequest,
            cancellationToken
        ).ConfigureAwait(false);
        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var aggregator = new CompletionAggregator(invocation, observer);
        aggregator.MergeUsage(
            PromptCacheTelemetryContext.Create(
                invocationOptions.PromptCacheReuseHint,
                PromptCacheSupportStatus.Unknown,
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["mapping"] = "implicit-best-effort"
                }
            )
        );
        var parser = new OpenAIResponsesStreamParser(
            _sanitizeProviderErrors
        );
        bool stoppedEarly = false;

        try {
            await foreach (CompletionSseFrame frame in
                CompletionSseEventReader.ReadFramesAsync(
                    stream,
                    cancellationToken
                )) {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Data is null) { continue; }

                if (string.Equals(
                        frame.Data,
                        "[DONE]",
                        StringComparison.Ordinal
                    )) {
                    CompletionStreamTermination.RequireTerminalEvent(
                        parser.TerminalEventObserved,
                        _streamDisplayName
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
                aggregator.MarkIncomplete(
                    detail: $"Streaming observer stopped {_streamDisplayName} completion early."
                );
            }
            else {
                CompletionStreamTermination.RequireTerminalEvent(
                    parser.TerminalEventObserved,
                    _streamDisplayName
                );
            }
        }
        catch (Exception exception) {
            CleanupAfterFailure(parser, aggregator, exception);
            if (_sanitizeProviderErrors
                && exception is InvalidDataException) {
                throw new OpenAICodexResponsesException(
                    OpenAICodexResponsesFailureReason
                        .ProtocolCompatibilityFailure,
                    "ChatGPT Codex stream violated the expected Responses protocol."
                );
            }
            throw;
        }

        DebugUtil.Trace(
            DebugCategory,
            $"[{_providerLabel}] Stream completed"
        );
        return aggregator.Build();
    }

    private void CleanupAfterFailure(
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
                $"[{_providerLabel}] Parser cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }

        try {
            aggregator.AbortIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[{_providerLabel}] Observer cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }
    }
}
