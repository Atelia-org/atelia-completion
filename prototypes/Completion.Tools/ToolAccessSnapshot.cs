using System.Collections.Immutable;

namespace Atelia.Completion.Tools;

/// <summary>
/// 不可变的工具访问快照：表达某个 <see cref="ToolSession"/> 当前允许看见 / 执行哪些工具。
/// </summary>
/// <remarks>
/// 当前形态是一组「隐藏工具名」，本质是一份 capability 快照，而不是规则解释器。
/// 它刻意不承诺 allow/deny 优先级、动态谓词或环境条件等更复杂的策略语义；
/// 若未来确有此类需求，应另行引入编译器把规则编译成本快照，而非就地膨胀本类型。
/// </remarks>
public sealed class ToolAccessSnapshot {
    private readonly ImmutableHashSet<string> _hiddenToolNames;

    public ToolAccessSnapshot(IEnumerable<string>? hiddenToolNames = null) {
        _hiddenToolNames = hiddenToolNames is null
            ? ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase)
            : ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, hiddenToolNames);
    }

    public static ToolAccessSnapshot AllowAll { get; } = new();

    public IReadOnlySet<string> HiddenToolNames => _hiddenToolNames;

    public bool IsVisible(string toolName) {
        if (string.IsNullOrWhiteSpace(toolName)) { return false; }
        return !_hiddenToolNames.Contains(toolName);
    }

    public bool IsExecutable(string toolName) => IsVisible(toolName);
}
