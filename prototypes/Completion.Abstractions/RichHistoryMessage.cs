namespace Atelia.Completion.Abstractions;

/// <summary>
/// Provider-neutral 的富 action message 接口，供能识别有序内容块的 converter 使用。
/// 这里只表达“要发给模型的 assistant 内容长什么样”，不承载 Agent 框架私有的 Turn / Invocation 元信息。
/// </summary>
public interface IRichActionMessage : IActionMessage {
    IReadOnlyList<ActionBlock> Blocks { get; }
}

/// <summary>
/// Assistant message 的有序内容块基类。开放式 sum type：
/// 当前 v1 已识别的子类型为 <see cref="Text"/> / <see cref="ToolCall"/> / <see cref="Thinking"/>；
/// 未来扩展点（v1 不实现）见 <c>docs/Agent/Thinking-Replay-Design.md §9</c>：
/// <c>Citation</c> / <c>ServerToolUse</c> / <c>SafetyMeta</c> 等。
/// </summary>
public abstract record ActionBlock {
    private protected ActionBlock() { }

    public abstract ActionBlockKind Kind { get; }

    /// <summary>
    /// 表示一段连续的文本内容。<see cref="Content"/> 反映 provider 流式协议中
    /// 一个完整 text block 的累计文本，不包含跨 block 的人为换行。
    /// </summary>
    public sealed record Text(string Content) : ActionBlock {
        public override ActionBlockKind Kind => ActionBlockKind.Text;
    }

    /// <summary>
    /// 表示一次工具调用请求，由 <see cref="Call"/> 描述具体名称、调用 ID 与参数。
    /// </summary>
    public sealed record ToolCall(ParsedToolCall Call) : ActionBlock {
        public override ActionBlockKind Kind => ActionBlockKind.ToolCall;
    }

    /// <summary>
    /// 表示一段 provider 提供的 thinking / reasoning 内容。
    /// <para>
    /// <see cref="Origin"/> 记录产生该 block 的具体调用来源（与 Turn lock 同构），
    /// 投影层用 <c>Origin == TargetInvocation</c> 判定 replay 兼容性。
    /// </para>
    /// <para>
    /// <see cref="OpaquePayload"/> 是 provider-native 的序列化字节，由 StreamParser 直接构造，
    /// Agent.Core / Accumulator 不参与解释。具体 converter 反向回灌时按需反序列化。
    /// 详见 <c>docs/Agent/Thinking-Replay-Design.md §3.1 / §5.2</c>。
    /// </para>
    /// <para>
    /// <see cref="PlainTextForDebug"/> 仅供日志/UI/调试展示，<b>不参与回灌</b>。
    /// </para>
    /// </summary>
    public sealed record Thinking(
        CompletionDescriptor Origin,
        System.ReadOnlyMemory<byte> OpaquePayload,
        string? PlainTextForDebug = null
    ) : ActionBlock {
        public override ActionBlockKind Kind => ActionBlockKind.Thinking;
    }
}

/// <summary>
/// <see cref="ActionBlock"/> 的判别枚举。新增 sum type 子类型时同步扩展此枚举。
/// </summary>
public enum ActionBlockKind {
    Text,
    ToolCall,
    Thinking,
    // 未来扩展（v1 不实现）：Citation / ServerToolUse / SafetyMeta
}
