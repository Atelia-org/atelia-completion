using System.Collections.Immutable;

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
/// Provider-neutral 的富 action message 接口，供能识别有序内容块的 converter 使用。
/// 这里只表达“要发给模型的 assistant 内容长什么样”，不承载 Agent 框架私有的 Turn / Invocation 元信息。
/// </summary>
public interface IActionMessage : IHistoryMessage {
    IReadOnlyList<ActionBlock> Blocks { get; }

    /// <summary>
    /// 从 <see cref="Blocks"/> 中无损派生的工具调用视图。默认实现遍历 <see cref="ActionBlock.ToolCall"/> 块。
    /// </summary>
    IReadOnlyList<ParsedToolCall> ToolCalls => Blocks
        .OfType<ActionBlock.ToolCall>()
        .Select(static block => block.Call)
        .ToArray();
}

/// <summary>
/// Canonical assistant/action 消息 DTO。只表达"发给模型的 assistant 内容长什么样"，
/// 是 <see cref="IActionMessage"/> 的最小具体实现。
/// </summary>
/// <remarks>
/// 与 <see cref="AggregatedAction"/>（流聚合快照，带 Invocation/Usage/Errors）不同，
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
public sealed record ActionMessage : IActionMessage {
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

// TODO: Thinking/Reasoning模型的CompletionTokens语义问题，不同厂商的原始值语义略有不同。需要明确：1. 计费相关的总补全长度。2. 后续输入相关的剔除Thinking/Reasoning部分后的正文长度
/// <summary>
/// 统一记录模型调用的 Token 使用情况，以满足计费和策略评估的需求。
/// 字段命名沿用主流模型厂商的术语，但在 Agent 侧可分别解释为"观测编码成本"、"动作生成成本"以及缓存复用效率。
/// </summary>
/// <param name="PromptTokens">本次调用中，输入历史所占用的 Token 数量。</param>
/// <param name="CompletionTokens">模型生成动作（输出）所消耗的 Token 数量。</param>
/// <param name="CachedPromptTokens">可选的、从缓存中命中的 Token 数量，反映了增量上下文所带来的成本节省。</param>
public record TokenUsage(int PromptTokens, int CompletionTokens, int? CachedPromptTokens = null);
// Gemini的ThinkingSignature问题，可能GTP-5也有，加密后的Thinking文本，跨模型不通用。源于公共安全审查，模型思考时的输出难以复合公共安全标准，所以加密了。

/// <summary>
/// 扩展方法，提供对 <see cref="IActionMessage"/> 的 lossy 派生视图。
/// </summary>
public static class ActionMessageExtensions {
    /// <summary>
    /// 将 <see cref="IActionMessage.Blocks"/> 中所有 <see cref="ActionBlock.Text"/> 块
    /// 按顺序拼接为单条字符串（无分隔符）。
    /// </summary>
    /// <remarks>
    /// <b>这是 lossy view，不是真相源。</b>
    /// 会丢失 text/tool-call/thinking 的边界和 interleaving 信息。
    /// 仅用于日志、调试和兼容性断言。任何依赖结构边界的下游应直接读取 <see cref="IActionMessage.Blocks"/>。
    /// </remarks>
    public static string GetFlattenedText(this IActionMessage message)
        => string.Concat(
            message.Blocks.OfType<ActionBlock.Text>().Select(static b => b.Content)
        );
}
