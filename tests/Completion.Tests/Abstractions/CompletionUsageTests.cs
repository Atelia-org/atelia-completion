using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tests.Abstractions;

public sealed class CompletionUsageTests {
    [Fact]
    public void ConstructorFreezesProviderDiagnostics() {
        var diagnostics = new Dictionary<string, string> {
            ["mode"] = "explicit"
        };
        var telemetry = new PromptCacheTelemetry(
            providerDiagnostics: diagnostics
        );

        diagnostics["mode"] = "mutated";
        diagnostics["late"] = "value";

        Assert.Equal("explicit", telemetry.ProviderDiagnostics!["mode"]);
        Assert.False(telemetry.ProviderDiagnostics.ContainsKey("late"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)telemetry.ProviderDiagnostics)
                .Add("new", "value")
        );
    }

    [Fact]
    public void MergeReplacesCumulativeSnapshotsAndCombinesSplitCounters() {
        CompletionUsage usage = new CompletionUsage(
            uncachedInputTokens: 10,
            cacheReadInputTokens: 4,
            outputTokens: 2,
            promptCache: new PromptCacheTelemetry(
                observationStatus: PromptCacheObservationStatus.Partial
            )
        );

        usage = usage.Merge(
            new CompletionUsage(
                uncachedInputTokens: 12,
                cacheCreationInputTokens: 5,
                outputTokens: 3,
                promptCache: new PromptCacheTelemetry(
                    observationStatus: PromptCacheObservationStatus.Partial
                )
            )
        );

        Assert.Equal(12, usage.UncachedInputTokens);
        Assert.Equal(5, usage.CacheCreationInputTokens);
        Assert.Equal(4, usage.CacheReadInputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal(
            PromptCacheObservationStatus.Complete,
            usage.PromptCache.ObservationStatus
        );
    }

    [Fact]
    public void CompleteDoubleZeroMeansNoIoNotProviderMissReason() {
        var usage = new CompletionUsage(
            cacheCreationInputTokens: 0,
            cacheReadInputTokens: 0,
            promptCache: new PromptCacheTelemetry(
                PromptCacheRequestStatus.Requested,
                PromptCacheSupportStatus.Supported,
                PromptCacheObservationStatus.Complete
            )
        );
        var unknown = new CompletionUsage(
            cacheReadInputTokens: 0,
            promptCache: new PromptCacheTelemetry(
                observationStatus: PromptCacheObservationStatus.Partial
            )
        );

        Assert.True(usage.IsNoCacheIoObserved);
        Assert.False(unknown.IsNoCacheIoObserved);
    }
}
