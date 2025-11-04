using System.Collections.Immutable;

namespace Atelia.Completion.Abstractions;

public interface IHistoryMessage {
    HistoryMessageKind Kind { get; }
    DateTimeOffset Timestamp { get; }
}

public interface IActionMessage : IHistoryMessage {
    string Contents { get; }
    IReadOnlyList<ParsedToolCall> ToolCalls { get; }
}

public record class ObservationMessage(
    DateTimeOffset Timestamp,
    /// <summary>
    /// 描述变化，来自于AgentState.History。
    /// 先用拼接好的string作为类型简化首版实现，以后再按需扩展为支持多模态和可保持element边界。
    /// </summary>
    string? Notifications,

    /// <summary>
    /// 描述状态，来自于对Apps的渲染。
    /// </summary>
    string? Windows

) : IHistoryMessage {
    public virtual HistoryMessageKind Kind => HistoryMessageKind.Observation;
}

public record ToolResultsMessage(
    DateTimeOffset Timestamp,
    string? Notifications,
    string? Windows,
    IReadOnlyList<ToolResult> Results,
    string? ExecuteError
) : ObservationMessage(Timestamp, Notifications, Windows) {
    public override HistoryMessageKind Kind => HistoryMessageKind.ToolResults;
}

public enum HistoryMessageKind {
    Observation,
    Action,
    ToolResults
}

// TODO: Thinking/Reasoning模型的CompletionTokens语义问题，不同厂商的原始值语义略有不同。需要明确：1. 计费相关的总补全长度。2. 后续输入相关的剔除Thinking/Reasoning部分后的正文长度
public record TokenUsage(int PromptTokens, int CompletionTokens, int? CachedPromptTokens = null);
// Gemini的ThinkingSignature问题，可能GTP-5也有，加密后的Thinking文本，跨模型不通用。源于公共安全审查，模型思考时的输出难以复合公共安全标准，所以加密了。
