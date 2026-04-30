namespace Atelia.Completion.Abstractions;

/// <summary>
/// 表示 Agent 历史中的一个事件，是对强化学习（RL）中 <c>history</c> 概念的抽象。
/// 此接口旨在屏蔽不同聊天或助手（Chat/Assistant）服务提供商在角色命名上的差异，从而允许上层 Agent 能够以统一的方式处理观测（Observation）与动作（Action）序列。
/// </summary>
public interface IHistoryMessage {
    /// <summary>
    /// 指示该历史事件的类型。其命名遵循强化学习（RL）中的 <c>Observation</c> 和 <c>Action</c> 术语，而非各服务提供商常用的 <c>user</c> 或 <c>assistant</c> 等角色名称。
    /// </summary>
    HistoryMessageKind Kind { get; }
}

/// <summary>

/// Canonical assistant/action 消息 DTO。只表达"发给模型的 assistant 内容长什么样"，
/// 是 <see cref="IHistoryMessage"/> 的具体实现，直接承载有序内容块。
/// </summary>
/// <remarks>
/// 与 <see cref="CompletionResult"/>（流聚合快照，带 Invocation/Errors）不同，
/// 本类型是无 invocation metadata 的纯消息体。适用于：
/// <list type="bullet">
/// <item>JSON/XML fixture 序列化与反序列化（测试数据集、跑分输入）</item>
/// <item>provider converter 的纯 action 回灌输入</item>
/// <item>投影层（Agent.Core）过滤 thinking block 后的产出</item>
/// </list>
/// <para>
/// <see cref="Blocks"/> 是唯一真相源。<see cref="GetFlattenedText"/> 是 lossy derived view，
/// 仅用于日志、调试和兼容性断言。
/// </para>
/// </remarks>
/// <param name="blocks">按 provider 实际生成顺序保存的内容块；构造时冻结为只读快照。</param>
public sealed record ActionMessage : IHistoryMessage {
    /// <summary>
    /// 按 provider 实际生成顺序保存的内容块，构造后冻结为只读快照。
    /// </summary>
    public IReadOnlyList<ActionBlock> Blocks { get; }

    /// <summary>
    /// 创建 <see cref="ActionMessage"/> 并冻结 <paramref name="blocks"/>。
    /// </summary>
    public ActionMessage(IReadOnlyList<ActionBlock> blocks) {
        ArgumentNullException.ThrowIfNull(blocks);
        Blocks = Array.AsReadOnly(blocks.ToArray());
    }

    /// <inheritdoc />
    public HistoryMessageKind Kind => HistoryMessageKind.Action;

    /// <summary>
    /// Lossy derived view：将 <see cref="Blocks"/> 中所有 <see cref="ActionBlock.Text"/>
    /// 块的内容按顺序串接（无分隔符）。非真相源——优先使用 <see cref="Blocks"/>。
    /// </summary>
    public string GetFlattenedText() => string.Concat(
        Blocks.OfType<ActionBlock.Text>().Select(static block => block.Content)
    );

    /// <summary>
    /// Lossy derived view：从 <see cref="Blocks"/> 中按顺序提取所有
    /// <see cref="ActionBlock.ToolCall"/> 块的工具调用信息。
    /// </summary>
    public IReadOnlyList<ParsedToolCall> ToolCalls => Blocks
        .OfType<ActionBlock.ToolCall>()
        .Select(static block => block.Call)
        .ToArray();
}

/// <summary>
/// 观测消息的基础形态。它将环境反馈（RL 术语）与聊天/助手（Chat/Assistant）场景中的系统或工具消息进行统一编码。
/// 为兼容不同来源的观测内容，引入统一的文本字段，可按需拼接通知增量与窗口状态等信息。
/// </summary>
/// <param name="Content">统一后的观测文本内容。</param>
public record class ObservationMessage(
    /// <summary>
    /// 统一后的观测文本内容，按需拼接通知增量与窗口状态等来源。
    /// </summary>
    string? Content
) : IHistoryMessage {
    /// <inheritdoc />
    public virtual HistoryMessageKind Kind => HistoryMessageKind.Observation;
}

/// <summary>
/// 在基础观测之上增加了工具执行结果。此消息兼容聊天（Chat）范式中的 "tool" 角色，同时在强化学习（RL）语境下仍被视为环境反馈的一部分。
/// </summary>
/// <param name="Content">与工具执行相关的观测文本内容。</param>
/// <param name="Results">工具执行产生的结构化结果列表。</param>
/// <param name="ExecuteError">若工具执行失败，此字段用于承载相关的错误信息。</param>
public record ToolResultsMessage(
    string? Content,
    IReadOnlyList<ToolResult> Results,
    string? ExecuteError
) : ObservationMessage(Content) {
    /// <inheritdoc />
    public override HistoryMessageKind Kind => HistoryMessageKind.ToolResults;
}

/// <summary>
/// 定义历史消息的类型。命名遵循强化学习（RL）的术语，为未来向更高级的 Agentic 模式演进预留空间，其内部可映射到不同服务提供商的角色定义。
/// </summary>
public enum HistoryMessageKind {
    /// <summary>
    /// 从环境到 Agent 的观测信息。
    /// </summary>
    Observation,
    /// <summary>
    /// 由 Agent 发出的动作指令。
    /// </summary>
    Action,
    /// <summary>
    /// 工具执行完成后产生的额外观测。
    /// </summary>
    ToolResults
}
