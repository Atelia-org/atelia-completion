using System.Collections.Immutable;

namespace Atelia.Completion.Tools;

public sealed class ToolAccessPolicy {
    private readonly ImmutableHashSet<string> _hiddenToolNames;

    public ToolAccessPolicy(IEnumerable<string>? hiddenToolNames = null) {
        _hiddenToolNames = hiddenToolNames is null
            ? ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase)
            : ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, hiddenToolNames);
    }

    public static ToolAccessPolicy AllowAll { get; } = new();

    public IReadOnlySet<string> HiddenToolNames => _hiddenToolNames;

    public bool IsVisible(string toolName) {
        if (string.IsNullOrWhiteSpace(toolName)) { return false; }
        return !_hiddenToolNames.Contains(toolName);
    }

    public bool IsExecutable(string toolName) => IsVisible(toolName);
}
