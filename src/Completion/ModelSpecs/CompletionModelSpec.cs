using Atelia.Completion.Abstractions;

namespace Atelia.Completion.ModelSpecs;

/// <summary>Immutable parameter knowledge for a model on a selected provider surface.</summary>
/// <remarks>
/// Missing fields do not inherit from other matching rules. Custom mappers must remain
/// immutable and thread-safe for the lifetime of every client using this specification.
/// </remarks>
public sealed record CompletionModelSpec {
    private int? _outputTokenLimit;

    public ICompletionReasoningEffortMapper? ReasoningMapper { get; init; }

    /// <summary>
    /// Positive output limit selected by this specification. Built-ins describe model
    /// maxima; hosts may supply an explicit fallback assumption for unknown models.
    /// </summary>
    public int? OutputTokenLimit {
        get => _outputTokenLimit;
        init {
            if (value is <= 0) {
                throw new ArgumentOutOfRangeException(nameof(OutputTokenLimit), "Output token limits must be positive.");
            }

            _outputTokenLimit = value;
        }
    }

    /// <summary>
    /// Explicit model support for forced tool selection on the current adapter surface.
    /// Null preserves conservative adapter validation; false rejects forced selection.
    /// </summary>
    public bool? ForcedToolChoiceSupported { get; init; }
}

/// <summary>Normalized intent and an optional provider-specific effort name.</summary>
/// <remarks>
/// EffectiveEffort is Disabled or an enabled effort, never ProviderDefault. An enabled
/// result from a custom mapper requires a nonblank name on string-effort adapters;
/// null is valid for standard close controls or adapters exposing only a thinking switch.
/// When there is no mapper, the internal resolver leaves naming to the adapter. Custom
/// names require an adapter that can consume them and do not determine thinking state.
/// </remarks>
public readonly record struct ReasoningEffortMapping(
    CompletionReasoningEffort EffectiveEffort,
    string? WireLevel);

/// <summary>Pure mapping of the six explicit reasoning intents.</summary>
/// <remarks>
/// Implementations must be deterministic, immutable and safe for concurrent calls;
/// freeze captured input collections during construction. ProviderDefault is handled
/// before this interface is called and cannot be overridden. Results must use a defined
/// explicit effort and a null or nonblank wire name. Invalid results are configuration
/// errors. Protocol field projection and I/O remain the adapter's responsibility.
/// </remarks>
public interface ICompletionReasoningEffortMapper {
    ReasoningEffortMapping Map(CompletionReasoningEffort requested);
}

internal static class ModelSpecReasoning {
    internal static ReasoningEffortMapping? Resolve(
        CompletionReasoningEffort requested,
        CompletionModelSpec? spec
    ) {
        if (requested is CompletionReasoningEffort.ProviderDefault) {
            return null;
        }

        ValidateExplicitEffort(requested, nameof(requested));
        if (spec?.ReasoningMapper is not { } mapper) {
            return new(requested, null);
        }

        ReasoningEffortMapping result = mapper.Map(requested);
        if (!IsExplicitEffort(result.EffectiveEffort)
            || (result.WireLevel is not null && string.IsNullOrWhiteSpace(result.WireLevel))) {
            throw new ArgumentException("The reasoning mapper returned an invalid mapping.", nameof(spec));
        }

        return result;
    }

    internal static void ValidateExplicitEffort(CompletionReasoningEffort effort, string parameterName) {
        if (!IsExplicitEffort(effort)) {
            throw new ArgumentOutOfRangeException(parameterName, "Expected an explicit reasoning effort.");
        }
    }

    private static bool IsExplicitEffort(CompletionReasoningEffort effort) => effort is
        CompletionReasoningEffort.Disabled or CompletionReasoningEffort.Low or
        CompletionReasoningEffort.Medium or CompletionReasoningEffort.High or
        CompletionReasoningEffort.XHigh or CompletionReasoningEffort.Max;
}
