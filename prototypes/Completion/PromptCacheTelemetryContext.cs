using Atelia.Completion.Abstractions;

namespace Atelia.Completion;

internal static class PromptCacheTelemetryContext {
    internal static CompletionUsage Create(
        PromptCacheReuseHint reuseHint,
        PromptCacheSupportStatus supportStatus,
        IReadOnlyDictionary<string, string>? providerDiagnostics = null
    ) => new(
        promptCache: new PromptCacheTelemetry(
            requestStatus: reuseHint switch {
                PromptCacheReuseHint.ConnectionDefault =>
                    PromptCacheRequestStatus.Unknown,
                PromptCacheReuseHint.NoReuseExpected =>
                    PromptCacheRequestStatus.NotRequested,
                PromptCacheReuseHint.ReuseExpectedSoon or
                PromptCacheReuseHint.ReuseExpectedAfterPause =>
                    PromptCacheRequestStatus.Requested,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(reuseHint),
                    reuseHint,
                    "Unknown prompt cache reuse hint."
                )
            },
            supportStatus,
            PromptCacheObservationStatus.Unknown,
            providerDiagnostics
        )
    );
}
