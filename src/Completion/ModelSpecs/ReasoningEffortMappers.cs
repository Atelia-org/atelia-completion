using Atelia.Completion.Abstractions;

namespace Atelia.Completion.ModelSpecs;

/// <summary>Factories for immutable supported-level mappings using the shared rounding policy.</summary>
public static class ReasoningEffortMappers {
    private static readonly CompletionReasoningEffort[] EnabledOrder = [
        CompletionReasoningEffort.Low, CompletionReasoningEffort.Medium,
        CompletionReasoningEffort.High, CompletionReasoningEffort.XHigh,
        CompletionReasoningEffort.Max
    ];

    /// <summary>
    /// Freezes a nonempty enabled-level set and uses standard low/medium/high/xhigh/max names.
    /// When supported, Disabled leaves its wire name to the adapter's standard close control.
    /// </summary>
    public static ICompletionReasoningEffortMapper ForSupportedLevels(
        IReadOnlyList<CompletionReasoningEffort> levels,
        bool supportsDisabled = false
    ) {
        ArgumentNullException.ThrowIfNull(levels);
        var names = new Dictionary<CompletionReasoningEffort, string?>();
        foreach (CompletionReasoningEffort level in levels) {
            int rank = Rank(level);
            if (rank < 0 || !names.TryAdd(level, StandardName(level))) {
                throw new ArgumentException("Enabled levels must be defined, unique and nonempty.", nameof(levels));
            }
        }

        if (supportsDisabled) {
            names.Add(CompletionReasoningEffort.Disabled, null);
        }

        return new LevelMapper(names);
    }

    /// <summary>
    /// Freezes the names of actual supported levels. Optional Disabled supplies a custom
    /// close name; it does not participate in enabled rounding. Other missing inputs use
    /// the common policy rather than defining a separate input-redirection table.
    /// </summary>
    public static ICompletionReasoningEffortMapper ForNamedLevels(
        IReadOnlyDictionary<CompletionReasoningEffort, string> names
    ) {
        ArgumentNullException.ThrowIfNull(names);
        var frozen = new Dictionary<CompletionReasoningEffort, string?>();
        foreach (var (level, name) in names) {
            if ((level != CompletionReasoningEffort.Disabled && Rank(level) < 0)
                || string.IsNullOrWhiteSpace(name) || !frozen.TryAdd(level, name)) {
                throw new ArgumentException("Named levels must be defined explicit efforts with nonblank unique names.", nameof(names));
            }
        }

        return new LevelMapper(frozen);
    }

    private static int Rank(CompletionReasoningEffort effort) => Array.IndexOf(EnabledOrder, effort);

    private static string StandardName(CompletionReasoningEffort effort) => effort switch {
        CompletionReasoningEffort.Low => "low",
        CompletionReasoningEffort.Medium => "medium",
        CompletionReasoningEffort.High => "high",
        CompletionReasoningEffort.XHigh => "xhigh",
        CompletionReasoningEffort.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(effort))
    };

    private sealed class LevelMapper : ICompletionReasoningEffortMapper {
        private readonly Dictionary<CompletionReasoningEffort, string?> _names;
        private readonly CompletionReasoningEffort[] _enabled;

        internal LevelMapper(Dictionary<CompletionReasoningEffort, string?> names) {
            _names = names;
            _enabled = EnabledOrder.Where(names.ContainsKey).ToArray();
            if (_enabled.Length == 0) {
                throw new ArgumentException("At least one enabled reasoning level is required.", nameof(names));
            }
        }

        public ReasoningEffortMapping Map(CompletionReasoningEffort requested) {
            ModelSpecReasoning.ValidateExplicitEffort(requested, nameof(requested));
            if (_names.TryGetValue(requested, out string? exactName)) {
                return new(requested, exactName);
            }

            CompletionReasoningEffort effective;
            int rank = Rank(requested);
            if (requested is CompletionReasoningEffort.Disabled) {
                effective = _enabled[0];
            }
            else if (requested is CompletionReasoningEffort.Medium) {
                effective = _enabled.OrderBy(level => Math.Abs(Rank(level) - rank))
                    .ThenByDescending(Rank).First();
            }
            else if (rank < Rank(CompletionReasoningEffort.Medium)) {
                effective = _enabled.FirstOrDefault(level => Rank(level) > rank, _enabled[^1]);
            }
            else {
                effective = _enabled.LastOrDefault(level => Rank(level) < rank, _enabled[0]);
            }

            return new(effective, _names[effective]);
        }
    }
}
