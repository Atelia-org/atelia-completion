using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion;

/// <summary>
/// 聚合 provider 流式输出的共享工具。
/// 供 <see cref="ICompletionClient"/> 实现者在内部循环中调用类型化方法增量喂入，
/// 最后调用 <see cref="Build"/> 产出完整快照。
/// 可选择性持有 <see cref="CompletionStreamObserver"/> 以支持流式观察与早停。
/// </summary>
/// <remarks>
/// <para>
/// <b>契约</b>：
/// <list type="bullet">
/// <item>连续 <see cref="AppendContent"/> 片段合并为单个 <see cref="ActionBlock.Text"/>；
/// text/tool_call/thinking 边界处会切块以保留顺序。</item>
/// <item>每个 <see cref="AppendToolCall"/> 直接转为 <see cref="ActionBlock.ToolCall"/>。</item>
/// <item>每个 <see cref="EndThinking"/> 接收由 parser 构造的具体 <see cref="ActionBlock.ReasoningBlock"/> 子类型，
/// 并校验其 <c>Origin</c> 与当前调用一致。</item>
/// <item>若需流式 thinking 观察（早停、UI 状态），使用 <see cref="BeginThinking"/> /
/// <see cref="AppendReasoningDelta"/> / <see cref="EndThinking"/> 三件套。</item>
/// <item>所有 <see cref="AppendError"/> 文本被收集到 <see cref="CompletionResult.Errors"/>，
/// <b>不抛异常</b>——调用方自行决定是否中断或上报。</item>
/// <item>若流末尾无任何块，会补一个空 <see cref="ActionBlock.Text"/> 以保证下游消费者的形状确定性。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class CompletionAggregator {
    private readonly CompletionDescriptor _invocation;
    private readonly CompletionStreamObserver? _observer;
    private readonly List<ActionBlock> _blocks = new();
    private readonly StringBuilder _contentBuilder = new();
    private List<string>? _errors;
    private bool _thinkingInProgress;

    public CompletionAggregator(CompletionDescriptor invocation, CompletionStreamObserver? observer = null) {
        _invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        _observer = observer;
    }

    /// <summary>
    /// 透传自 <see cref="CompletionStreamObserver.ShouldStop"/>，供客户端在 SSE 循环中检查是否应提前终止。
    /// </summary>
    public bool ShouldStop => _observer?.ShouldStop == true;

    /// <summary>
    /// 当前调用来源描述符。StreamParser 用它为 <see cref="ActionBlock.ReasoningBlock"/> 子类型填充 <c>Origin</c>。
    /// </summary>
    public CompletionDescriptor Invocation => _invocation;

    /// <summary>
    /// 喂入一段文本内容。连续调用时片段自动合并；当后续调用 <see cref="AppendToolCall"/>、
    /// <see cref="AppendThinking"/> 或 <see cref="AppendError"/> 时会自动切块。
    /// </summary>
    public void AppendContent(string text) {
        if (!string.IsNullOrEmpty(text)) {
            _contentBuilder.Append(text);
            _observer?.OnTextDelta(text);
        }
    }

    /// <summary>
    /// 喂入一个已完成的工具调用请求。会先 flush 待定文本再追加块。
    /// </summary>
    public void AppendToolCall(RawToolCall toolCall) {
        FlushPendingText();
        _blocks.Add(new ActionBlock.ToolCall(toolCall));
        _observer?.OnToolCall(toolCall);
    }

    // ── Thinking 流式三件套 ──

    /// <summary>
    /// 标记 thinking 块开始。会先 flush 待定文本，然后通知 observer。
    /// 适用于需要逐 delta 观察 reasoning 内容或显示"思考中…"UI 的场景。
    /// </summary>
    public void BeginThinking() {
        if (_thinkingInProgress) { throw new InvalidOperationException("A thinking block is already in progress."); }

        FlushPendingText();
        _thinkingInProgress = true;
        _observer?.OnThinkingBegin();
    }

    /// <summary>
    /// 喂入一段明文 reasoning 增量文本。
    /// <b>仅适用于明文 reasoning</b>；加密/签名 reasoning 不会产生 delta。
    /// </summary>
    public void AppendReasoningDelta(string delta) {
        if (!string.IsNullOrEmpty(delta)) {
            _observer?.OnReasoningDelta(delta);
        }
    }

    /// <summary>
    /// 标记 thinking 块结束，将由 parser 构造的具体 <see cref="ActionBlock.ReasoningBlock"/> 子类型写入块列表。
    /// 校验 <c>block.Origin</c> 与当前调用的 <c>_invocation</c> 一致，不一致时抛 <see cref="InvalidOperationException"/>。
    /// 通知 observer thinking 结束（即使 reasoning 内容为加密状态也会通知）。
    /// </summary>
    /// <param name="block">由 StreamParser 构造的、Origin 已填充的具体 ReasoningBlock 子类型。</param>
    /// <exception cref="ArgumentNullException">当 <paramref name="block"/> 为 <see langword="null"/> 时。</exception>
    /// <exception cref="InvalidOperationException">当 Origin 不匹配时。</exception>
    public void EndThinking(ActionBlock.ReasoningBlock block) {
        if (block is null) { throw new ArgumentNullException(nameof(block)); }
        if (!Equals(block.Origin, _invocation)) {
            throw new InvalidOperationException(
                $"ReasoningBlock Origin mismatch: expected {_invocation}, got {block.Origin}."
            );
        }

        if (_thinkingInProgress) {
            _observer?.OnThinkingEnd();
            _thinkingInProgress = false;
        }

        _blocks.Add(block);
    }

    /// <summary>
    /// 喂入一个已完成的 thinking 块（便捷方法，等价于 BeginThinking + EndThinking 无 delta）。
    /// </summary>
    /// <param name="block">由 StreamParser 构造的、Origin 已填充的具体 ReasoningBlock 子类型。</param>
    public void AppendThinking(ActionBlock.ReasoningBlock block) {
        BeginThinking();
        EndThinking(block);
    }

    /// <summary>
    /// 丢弃未完成的流式中间状态。
    /// 用于 observer 早停等场景，确保不会把半成品 tool/thinking 状态错误地视作完整结果。
    /// </summary>
    public void AbortIncompleteStreamingState() {
        if (_thinkingInProgress) {
            _observer?.OnThinkingEnd();
            _thinkingInProgress = false;
        }
    }

    /// <summary>
    /// 喂入一条错误文本。错误不会抛异常，仅被收集到 <see cref="CompletionResult.Errors"/>。
    /// </summary>
    public void AppendError(string error) {
        FlushPendingText();
        if (!string.IsNullOrEmpty(error)) {
            _errors ??= new List<string>();
            _errors.Add(error);
        }
    }

    /// <summary>
    /// 产出最终的 <see cref="CompletionResult"/>。调用此方法后不应再调用任何 Append 方法。
    /// </summary>
    public CompletionResult Build() {
        FlushPendingText();

        if (_blocks.Count == 0) {
            // 形状确定性兜底：下游始终能拿到至少一个块。
            _blocks.Add(new ActionBlock.Text(string.Empty));
        }

        var message = new ActionMessage(_blocks);
        return new CompletionResult(message, _invocation, _errors);
    }

    private void FlushPendingText() {
        if (_contentBuilder.Length == 0) { return; }
        _blocks.Add(new ActionBlock.Text(_contentBuilder.ToString()));
        _contentBuilder.Clear();
    }
}
