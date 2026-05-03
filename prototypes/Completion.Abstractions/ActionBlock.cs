namespace Atelia.Completion.Abstractions;

/// <summary>
/// Assistant message 的有序内容块基类。开放式 sum type：
/// 当前 v1 已识别的子类型为 <see cref="Text"/> / <see cref="ToolCall"/> / <see cref="ReasoningBlock"/>；
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
    public sealed record ToolCall(RawToolCall Call) : ActionBlock {
        public override ActionBlockKind Kind => ActionBlockKind.ToolCall;
    }

    /// <summary>
    /// Reasoning / thinking 内容块的抽象基类。
    /// <para>
    /// <see cref="Origin"/> 记录产生该 block 的具体调用来源（与 Turn lock 同构），
    /// 投影层用 <c>Origin == TargetInvocation</c> 判定 replay 兼容性。
    /// </para>
    /// <para>
    /// 具体子类型分散在各自 provider 程序集中（如 <c>Completion.Anthropic</c>），
    /// 允许各 provider 以原生格式承载 reasoning 内容（明文、加密、签名等），
    /// 无需将异构格式强行归一化为单一 <c>OpaquePayload</c>。
    /// </para>
    /// <para>
    /// <see cref="PlainTextForDebug"/> 仅供日志/UI/调试展示，<b>不参与回灌</b>。
    /// </para>
    /// </summary>
    /// <param name="Origin">产生该 reasoning 的调用来源描述符。</param>
    /// <param name="PlainTextForDebug">可选明文，仅供日志/UI/调试使用。</param>
    public abstract record ReasoningBlock(
        CompletionDescriptor Origin,
        string? PlainTextForDebug = null
    ) : ActionBlock {
        /// <inheritdoc />
        public override ActionBlockKind Kind => ActionBlockKind.Thinking;
    }

    /// <summary>
    /// 明文 reasoning 块。承载 provider 已提取的明文字符串推理内容，
    /// 可用于跨 provider 的审计、日志、UI 展示。
    /// <para>
    /// <b>注意</b>：明文不代表可跨 provider 回灌——回灌兼容性仍由 <see cref="ReasoningBlock.Origin"/>
    /// 和具体 converter 的能力决定。
    /// </para>
    /// </summary>
    /// <param name="Content">明文推理文本。</param>
    /// <param name="Origin">产生该 reasoning 的调用来源描述符。</param>
    /// <param name="PlainTextForDebug">可选调试文本；默认与 <paramref name="Content"/> 相同。</param>
    public sealed record TextReasoningBlock(
        string Content,
        CompletionDescriptor Origin,
        string? PlainTextForDebug = null
    ) : ReasoningBlock(Origin, PlainTextForDebug ?? Content);
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
