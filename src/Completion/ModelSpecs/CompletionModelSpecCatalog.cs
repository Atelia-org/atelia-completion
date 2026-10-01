namespace Atelia.Completion.ModelSpecs;

/// <summary>Immutable model rules: exact ID, longest literal prefix, then global fallback.</summary>
/// <remarks>
/// Matching uses Ordinal case sensitivity and selects one complete specification.
/// Neither aliases nor wildcard rules rewrite the requested model ID. Input lists
/// and rule collections are copied; custom mapper immutability is the host's contract.
/// </remarks>
public sealed class CompletionModelSpecCatalog {
    private readonly Dictionary<string, CompletionModelSpec> _models;
    private readonly Dictionary<string, CompletionModelSpec> _patterns;
    private readonly (string Prefix, CompletionModelSpec Spec)[] _prefixes;

    public static CompletionModelSpecCatalog Empty { get; } = new();

    /// <summary>
    /// Creates a frozen directory. Each nonempty ID list expands into independent exact
    /// keys. Repeated IDs or patterns are errors, even when their specifications agree.
    /// </summary>
    public CompletionModelSpecCatalog(
        IEnumerable<(IReadOnlyList<string> ModelIds, CompletionModelSpec Spec)>? models = null,
        IEnumerable<(string Pattern, CompletionModelSpec Spec)>? patterns = null
    ) {
        _models = new(StringComparer.Ordinal);
        _patterns = new(StringComparer.Ordinal);
        if (models is not null) {
            foreach (var (ids, spec) in models) {
                ArgumentNullException.ThrowIfNull(spec);
                foreach (string id in CopyModelIds(ids)) {
                    if (!_models.TryAdd(id, spec)) {
                        throw new ArgumentException("Duplicate exact model ID.", nameof(models));
                    }
                }
            }
        }

        if (patterns is not null) {
            foreach (var (pattern, spec) in patterns) {
                ArgumentNullException.ThrowIfNull(spec);
                ValidatePattern(pattern);
                if (!_patterns.TryAdd(pattern, spec)) {
                    throw new ArgumentException("Duplicate model pattern.", nameof(patterns));
                }
            }
        }

        _prefixes = BuildPrefixes(_patterns);
    }

    private CompletionModelSpecCatalog(
        Dictionary<string, CompletionModelSpec> models,
        Dictionary<string, CompletionModelSpec> patterns
    ) {
        _models = models;
        _patterns = patterns;
        _prefixes = BuildPrefixes(patterns);
    }

    /// <summary>Returns the selected complete specification, or null when no rule matches.</summary>
    public CompletionModelSpec? Lookup(string modelId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (_models.TryGetValue(modelId, out var exact)) {
            return exact;
        }

        foreach (var (prefix, spec) in _prefixes) {
            if (modelId.StartsWith(prefix, StringComparison.Ordinal)) {
                return spec;
            }
        }

        return null;
    }

    /// <summary>Returns a new catalog, explicitly replacing this exact ID if present.</summary>
    public CompletionModelSpecCatalog WithModel(string modelId, CompletionModelSpec spec) =>
        WithModels([modelId], spec);

    /// <summary>
    /// Returns a new catalog replacing each listed exact key. The whole list is validated
    /// before updating; overriding one alias later does not affect its former companions.
    /// </summary>
    public CompletionModelSpecCatalog WithModels(IReadOnlyList<string> modelIds, CompletionModelSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);
        string[] ids = CopyModelIds(modelIds);
        var models = new Dictionary<string, CompletionModelSpec>(_models, StringComparer.Ordinal);
        foreach (string id in ids) {
            models[id] = spec;
        }

        return new(models, new(_patterns, StringComparer.Ordinal));
    }

    /// <summary>Returns a new catalog replacing a terminal-* prefix rule or the global * rule.</summary>
    public CompletionModelSpecCatalog WithPattern(string pattern, CompletionModelSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);
        ValidatePattern(pattern);
        var patterns = new Dictionary<string, CompletionModelSpec>(_patterns, StringComparer.Ordinal) {
            [pattern] = spec
        };
        return new(new(_models, StringComparer.Ordinal), patterns);
    }

    private static string[] CopyModelIds(IReadOnlyList<string> modelIds) {
        ArgumentNullException.ThrowIfNull(modelIds);
        string[] result = modelIds.ToArray();
        if (result.Length == 0) {
            throw new ArgumentException("An exact model ID list must not be empty.", nameof(modelIds));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in result) {
            ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(modelIds));
            if (ContainsGlobCharacter(id) || !seen.Add(id)) {
                throw new ArgumentException("Exact model IDs must be unique and must not contain glob syntax.", nameof(modelIds));
            }
        }

        return result;
    }

    private static void ValidatePattern(string pattern) {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (!pattern.EndsWith('*') || ContainsGlobCharacter(pattern[..^1])) {
            throw new ArgumentException("Only a single terminal '*' with a literal prefix is supported.", nameof(pattern));
        }
    }

    private static bool ContainsGlobCharacter(string value) => value.IndexOfAny(['*', '?', '[', ']']) >= 0;

    private static (string Prefix, CompletionModelSpec Spec)[] BuildPrefixes(
        Dictionary<string, CompletionModelSpec> patterns
    ) => patterns.Select(pair => (Prefix: pair.Key[..^1], Spec: pair.Value))
        .OrderByDescending(pair => pair.Prefix.Length).ToArray();
}
