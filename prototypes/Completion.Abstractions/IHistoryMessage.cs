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
/// 表示由 Agent 发出的一个动作。
/// 该接口将聊天（Chat）范式中对自然语言和工具调用的编码，统一为强化学习（RL）中的动作输出语义。
/// </summary>
public interface IActionMessage : IHistoryMessage {
    /// <summary>
    /// 模型生成的自然语言文本或命令，代表了策略（Policy）在当前历史状态下的决策输出。
    /// </summary>
    string Content { get; }

    /// <summary>
    /// 若模型决策中包含工具调用，此属性提供解析后的结构化信息，便于在环境中执行并计算其效果。
    /// </summary>
    IReadOnlyList<ParsedToolCall> ToolCalls { get; }
}

/// <summary>
/// 观测消息的基础形态。它将环境反馈（RL 术语）与聊天/助手（Chat/Assistant）场景中的系统或工具消息进行统一编码。
/// 为兼容不同来源的观测内容，引入统一的文本字段，可按需拼接通知增量与窗口状态等信息。
/// </summary>
/// <param name="Timestamp">观测生成的时间戳，用于维持历史序列的顺序。</param>
/// <param name="Content">统一后的观测文本内容。</param>
public record class ObservationMessage(
    DateTimeOffset Timestamp,
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
/// <param name="Timestamp">工具执行结果返回的时间戳。</param>
/// <param name="Content">与工具执行相关的观测文本内容。</param>
/// <param name="Results">工具执行产生的结构化结果列表。</param>
/// <param name="ExecuteError">若工具执行失败，此字段用于承载相关的错误信息。</param>
public record ToolResultsMessage(
    DateTimeOffset Timestamp,
    string? Content,
    IReadOnlyList<ToolResult> Results,
    string? ExecuteError
) : ObservationMessage(Timestamp, Content) {
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
