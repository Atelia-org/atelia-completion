using System.Collections.Immutable;
using System.Linq;

namespace Atelia.Completion.Tools;

public enum ToolAccessMode {
    AllowAllExceptHidden,
    AllowOnlyListed
}

/// <summary>
/// 不可变的工具访问快照：表达某个 <see cref="ToolSession"/> 当前允许看见 / 执行哪些工具。
/// </summary>
/// <remarks>
/// 当前形态只承诺两类简单 capability 快照：
/// 默认模式是“全部可见，排除隐藏名单”，也支持“仅允许列出的工具”。
/// 它刻意不承诺更复杂的规则解释、动态谓词或环境条件等策略语义；
/// 若未来确有此类需求，应另行引入编译器把规则编译成本快照，而非就地膨胀本类型。
/// </remarks>
public sealed class ToolAccessSnapshot {
    private static readonly ImmutableHashSet<string> EmptyToolNames = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    private readonly ImmutableHashSet<string> _toolNames;

    private ToolAccessSnapshot(ToolAccessMode mode, IEnumerable<string>? toolNames = null) {
        Mode = mode;
        _toolNames = toolNames is null
            ? EmptyToolNames
            : ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, toolNames);
    }

    public static ToolAccessSnapshot AllowAll { get; } = new(ToolAccessMode.AllowAllExceptHidden);

    public static ToolAccessSnapshot Hide(IEnumerable<string> hiddenToolNames) {
        ArgumentNullException.ThrowIfNull(hiddenToolNames);
        return new ToolAccessSnapshot(ToolAccessMode.AllowAllExceptHidden, hiddenToolNames);
    }

    public static ToolAccessSnapshot AllowOnly(IEnumerable<string> visibleToolNames) {
        ArgumentNullException.ThrowIfNull(visibleToolNames);
        return new ToolAccessSnapshot(ToolAccessMode.AllowOnlyListed, visibleToolNames);
    }

    /// <summary>
    /// 计算两个 capability 快照的交集。
    /// 结果表示“同时满足这两个快照时”最终仍可见 / 可执行的工具集合。
    /// </summary>
    public static ToolAccessSnapshot Intersect(ToolAccessSnapshot left, ToolAccessSnapshot right) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (ReferenceEquals(left, AllowAll)) { return right; }
        if (ReferenceEquals(right, AllowAll)) { return left; }

        return (left.Mode, right.Mode) switch {
            (ToolAccessMode.AllowAllExceptHidden, ToolAccessMode.AllowAllExceptHidden)
                => Hide(left._toolNames.Union(right._toolNames, StringComparer.OrdinalIgnoreCase)),

            (ToolAccessMode.AllowOnlyListed, ToolAccessMode.AllowOnlyListed)
                => AllowOnly(left._toolNames.Intersect(right._toolNames, StringComparer.OrdinalIgnoreCase)),

            (ToolAccessMode.AllowOnlyListed, ToolAccessMode.AllowAllExceptHidden)
                => AllowOnly(left._toolNames.Except(right._toolNames, StringComparer.OrdinalIgnoreCase)),

            (ToolAccessMode.AllowAllExceptHidden, ToolAccessMode.AllowOnlyListed)
                => AllowOnly(right._toolNames.Except(left._toolNames, StringComparer.OrdinalIgnoreCase)),

            _ => AllowAll
        };
    }

    public ToolAccessMode Mode { get; }

    public IReadOnlySet<string> HiddenToolNames => Mode == ToolAccessMode.AllowAllExceptHidden
        ? _toolNames
        : EmptyToolNames;

    public IReadOnlySet<string> VisibleToolNames => Mode == ToolAccessMode.AllowOnlyListed
        ? _toolNames
        : EmptyToolNames;

    public bool IsVisible(string toolName) {
        if (string.IsNullOrWhiteSpace(toolName)) { return false; }

        return Mode switch {
            ToolAccessMode.AllowAllExceptHidden => !_toolNames.Contains(toolName),
            ToolAccessMode.AllowOnlyListed => _toolNames.Contains(toolName),
            _ => false
        };
    }

    public bool IsExecutable(string toolName) => IsVisible(toolName);
}
