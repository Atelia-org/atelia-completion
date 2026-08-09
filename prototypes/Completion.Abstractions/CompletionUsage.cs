using System.Collections.ObjectModel;

namespace Atelia.Completion.Abstractions;

/// <summary>Whether one invocation asked the selected client to reuse its stable prompt prefix.</summary>
public enum PromptCacheRequestStatus {
    Unknown,
    NotRequested,
    Requested,
}

/// <summary>Whether the selected client surface can honor the requested cache policy.</summary>
public enum PromptCacheSupportStatus {
    Unknown,
    Unsupported,
    Supported,
}

/// <summary>Whether the provider returned authoritative cache usage fields for this invocation.</summary>
public enum PromptCacheObservationStatus {
    Unknown,
    Unavailable,
    Partial,
    Complete,
}

/// <summary>
/// Provider-neutral cache telemetry. Request intent, adapter support, and
/// provider observation are intentionally independent axes.
/// </summary>
public sealed record PromptCacheTelemetry {
    private IReadOnlyDictionary<string, string>? _providerDiagnostics;

    public PromptCacheTelemetry(
        PromptCacheRequestStatus requestStatus = PromptCacheRequestStatus.Unknown,
        PromptCacheSupportStatus supportStatus = PromptCacheSupportStatus.Unknown,
        PromptCacheObservationStatus observationStatus = PromptCacheObservationStatus.Unknown,
        IReadOnlyDictionary<string, string>? providerDiagnostics = null
    ) {
        if (!Enum.IsDefined(requestStatus)) {
            throw new ArgumentOutOfRangeException(nameof(requestStatus));
        }
        if (!Enum.IsDefined(supportStatus)) {
            throw new ArgumentOutOfRangeException(nameof(supportStatus));
        }
        if (!Enum.IsDefined(observationStatus)) {
            throw new ArgumentOutOfRangeException(nameof(observationStatus));
        }

        RequestStatus = requestStatus;
        SupportStatus = supportStatus;
        ObservationStatus = observationStatus;
        ProviderDiagnostics = providerDiagnostics;
    }

    public static PromptCacheTelemetry Unknown { get; } = new();

    public PromptCacheRequestStatus RequestStatus { get; }

    public PromptCacheSupportStatus SupportStatus { get; }

    public PromptCacheObservationStatus ObservationStatus { get; }

    public IReadOnlyDictionary<string, string>? ProviderDiagnostics {
        get => _providerDiagnostics;
        init => _providerDiagnostics = FreezeDiagnostics(value);
    }

    internal PromptCacheTelemetry Merge(PromptCacheTelemetry update) {
        ArgumentNullException.ThrowIfNull(update);
        return new PromptCacheTelemetry(
            update.RequestStatus is PromptCacheRequestStatus.Unknown
                ? RequestStatus
                : update.RequestStatus,
            update.SupportStatus is PromptCacheSupportStatus.Unknown
                ? SupportStatus
                : update.SupportStatus,
            (PromptCacheObservationStatus)Math.Max(
                (int)ObservationStatus,
                (int)update.ObservationStatus
            ),
            MergeDiagnostics(ProviderDiagnostics, update.ProviderDiagnostics)
        );
    }

    private static IReadOnlyDictionary<string, string>? FreezeDiagnostics(
        IReadOnlyDictionary<string, string>? diagnostics
    ) {
        if (diagnostics is null) { return null; }
        var frozen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in diagnostics) {
            if (string.IsNullOrWhiteSpace(key)) {
                throw new ArgumentException(
                    "Provider diagnostic keys cannot be blank.",
                    nameof(diagnostics)
                );
            }
            frozen.Add(
                key,
                value ?? throw new ArgumentException(
                    "Provider diagnostic values cannot be null.",
                    nameof(diagnostics)
                )
            );
        }
        return new ReadOnlyDictionary<string, string>(frozen);
    }

    private static IReadOnlyDictionary<string, string>? MergeDiagnostics(
        IReadOnlyDictionary<string, string>? current,
        IReadOnlyDictionary<string, string>? update
    ) {
        if (update is null) { return current; }
        if (current is null) { return update; }
        var merged = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach ((string key, string value) in update) {
            merged[key] = value;
        }
        return merged;
    }
}

/// <summary>
/// Optional normalized token usage. Non-null zeroes are authoritative values;
/// null means the provider did not report that dimension.
/// </summary>
public sealed record CompletionUsage {
    public CompletionUsage(
        long? uncachedInputTokens = null,
        long? cacheCreationInputTokens = null,
        long? cacheReadInputTokens = null,
        long? outputTokens = null,
        PromptCacheTelemetry? promptCache = null
    ) {
        UncachedInputTokens = RequireNonNegative(uncachedInputTokens, nameof(uncachedInputTokens));
        CacheCreationInputTokens = RequireNonNegative(cacheCreationInputTokens, nameof(cacheCreationInputTokens));
        CacheReadInputTokens = RequireNonNegative(cacheReadInputTokens, nameof(cacheReadInputTokens));
        OutputTokens = RequireNonNegative(outputTokens, nameof(outputTokens));
        PromptCache = promptCache ?? PromptCacheTelemetry.Unknown;
    }

    public static CompletionUsage Unknown { get; } = new();

    public long? UncachedInputTokens { get; }

    public long? CacheCreationInputTokens { get; }

    public long? CacheReadInputTokens { get; }

    public long? OutputTokens { get; }

    public PromptCacheTelemetry PromptCache { get; }

    /// <summary>
    /// True only when the provider emitted the complete read/write counter set
    /// and both values were zero. This does not identify a provider-side miss
    /// reason; the prefix may also have been ineligible or caching disabled.
    /// </summary>
    public bool IsNoCacheIoObserved =>
        PromptCache.ObservationStatus is PromptCacheObservationStatus.Complete
        && CacheReadInputTokens is 0
        && CacheCreationInputTokens is 0;

    public CompletionUsage Merge(CompletionUsage update) {
        ArgumentNullException.ThrowIfNull(update);
        long? creation = update.CacheCreationInputTokens
            ?? CacheCreationInputTokens;
        long? read = update.CacheReadInputTokens
            ?? CacheReadInputTokens;
        PromptCacheTelemetry promptCache = PromptCache.Merge(
            update.PromptCache
        );
        if (creation is not null
            && read is not null
            && promptCache.ObservationStatus
                is PromptCacheObservationStatus.Partial) {
            promptCache = new PromptCacheTelemetry(
                promptCache.RequestStatus,
                promptCache.SupportStatus,
                PromptCacheObservationStatus.Complete,
                promptCache.ProviderDiagnostics
            );
        }
        return new CompletionUsage(
            update.UncachedInputTokens ?? UncachedInputTokens,
            creation,
            read,
            update.OutputTokens ?? OutputTokens,
            promptCache
        );
    }

    private static long? RequireNonNegative(long? value, string parameterName) {
        if (value < 0) {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return value;
    }
}
