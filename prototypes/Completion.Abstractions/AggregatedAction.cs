using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// 一次 LLM 调用的流式 chunk 被聚合后的中性快照。直接实现 <see cref="IActionMessage"/>，
/// 可作为 <c>CompletionRequest.Context</c> 的下一轮 action 历史回灌。
/// <para>
/// 这是 <c>IAsyncEnumerable&lt;CompletionChunk&gt;.AggregateAsync(...)</c> 的标准产出。
/// 上层框架（如 Agent.Core）若需要附加自身私有元数据（serial、token estimate 等），
/// 应当 <b>包装</b> 本类型，而不是替代或扩展。
/// </para>
/// </summary>
/// <param name="Message">Canonical action 消息体；<see cref="ActionMessage.Blocks"/> 已在构造时冻结。</param>
/// <param name="Invocation">本次调用的来源描述符；<see cref="ActionBlock.Thinking.Origin"/> 与之对齐。</param>
/// <param name="Usage">流末尾结算的 token 用量；provider 未返回时为 <see langword="null"/>。</param>
/// <param name="Errors">流中通过 <see cref="CompletionChunkKind.Error"/> 报告的错误文本；无错误时为 <see langword="null"/>。</param>
public sealed record AggregatedAction : IActionMessage {
    private ActionMessage _message = null!;
    private CompletionDescriptor _invocation = null!;
    private IReadOnlyList<string>? _errors;

    /// <summary>
    /// 创建 <see cref="AggregatedAction"/>。
    /// </summary>
    public AggregatedAction(
        ActionMessage message,
        CompletionDescriptor invocation,
        TokenUsage? usage = null,
        IReadOnlyList<string>? errors = null
    ) {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        Usage = usage;
        Errors = errors;
    }

    /// <summary>Canonical action 消息体。</summary>
    public ActionMessage Message {
        get => _message;
        init => _message = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>本次调用的来源描述符；<see cref="ActionBlock.Thinking.Origin"/> 与之对齐。</summary>
    public CompletionDescriptor Invocation {
        get => _invocation;
        init => _invocation = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>流末尾结算的 token 用量；provider 未返回时为 <see langword="null"/>。</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>流中通过 <see cref="CompletionChunkKind.Error"/> 报告的错误文本；无错误时为 <see langword="null"/>。</summary>
    public IReadOnlyList<string>? Errors {
        get => _errors;
        init => _errors = value is null ? null : FreezeList(value);
    }

    // ── IActionMessage façade ── converter / 下游代码零改动 ──

    /// <inheritdoc />
    public IReadOnlyList<ActionBlock> Blocks => Message.Blocks;

    /// <inheritdoc />
    public HistoryMessageKind Kind => HistoryMessageKind.Action;

    /// <summary>
    /// Lossy derived view，委托给 <see cref="Message"/>。
    /// </summary>
    public string GetFlattenedText() => Message.GetFlattenedText();

    private static IReadOnlyList<T> FreezeList<T>(IReadOnlyList<T> items)
        => items.Count == 0 ? Array.AsReadOnly(Array.Empty<T>()) : Array.AsReadOnly(items.ToArray());
}

/// <summary>
/// 把 provider-neutral 的 <see cref="CompletionChunk"/> 流聚合为 <see cref="AggregatedAction"/> 的标准入口。
/// </summary>
public static class CompletionChunkAggregation {
    /// <summary>
    /// 按到达顺序消费 <paramref name="chunks"/>，输出一个保留 ordering、合并连续文本、
    /// 把 thinking payload 透明绑定到 <paramref name="invocation"/> 的中性 action 快照。
    /// <para>
    /// <b>契约</b>：
    /// <list type="bullet">
    /// <item>连续 <see cref="CompletionChunkKind.Content"/> 片段合并为单个 <see cref="ActionBlock.Text"/>；
    /// text/tool_call/thinking 边界处会切块以保留顺序。</item>
    /// <item>每个 <see cref="CompletionChunkKind.ToolCall"/> 直接转为 <see cref="ActionBlock.ToolCall"/>。</item>
    /// <item>每个 <see cref="CompletionChunkKind.Thinking"/> 被包装为 <see cref="ActionBlock.Thinking"/>，
    /// <see cref="ActionBlock.Thinking.Origin"/> = <paramref name="invocation"/>，
    /// <see cref="ThinkingChunk.OpaquePayload"/> 原样透传，<b>不解析</b>。</item>
    /// <item>所有 <see cref="CompletionChunkKind.Error"/> 文本被收集到 <see cref="AggregatedAction.Errors"/>，
    /// <b>不抛异常</b>——调用方自行决定是否中断或上报。</item>
    /// <item>若流末尾无任何块，会补一个空 <see cref="ActionBlock.Text"/> 以保证下游消费者的形状确定性。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static async Task<AggregatedAction> AggregateAsync(
        this IAsyncEnumerable<CompletionChunk> chunks,
        CompletionDescriptor invocation,
        CancellationToken cancellationToken = default
    ) {
        if (chunks is null) { throw new ArgumentNullException(nameof(chunks)); }
        if (invocation is null) { throw new ArgumentNullException(nameof(invocation)); }

        var blocks = new List<ActionBlock>();
        var contentBuilder = new StringBuilder();
        TokenUsage? tokenUsage = null;
        List<string>? errors = null;

        void FlushPendingText() {
            if (contentBuilder.Length == 0) { return; }
            blocks.Add(new ActionBlock.Text(contentBuilder.ToString()));
            contentBuilder.Clear();
        }

        await foreach (var delta in chunks.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            switch (delta.Kind) {
                case CompletionChunkKind.Content:
                    if (!string.IsNullOrEmpty(delta.Content)) {
                        contentBuilder.Append(delta.Content);
                    }
                    break;
                case CompletionChunkKind.ToolCall:
                    FlushPendingText();
                    if (delta.ToolCall is not null) {
                        blocks.Add(new ActionBlock.ToolCall(delta.ToolCall));
                    }
                    break;
                case CompletionChunkKind.Thinking:
                    FlushPendingText();
                    if (delta.Thinking is not null) {
                        // OpaquePayload 完全透明：本聚合器不解释 bytes，仅按到达顺序串入 Blocks。
                        blocks.Add(new ActionBlock.Thinking(
                            invocation,
                            delta.Thinking.OpaquePayload,
                            delta.Thinking.PlainTextForDebug
                        ));
                    }
                    break;
                case CompletionChunkKind.Error:
                    FlushPendingText();
                    if (!string.IsNullOrEmpty(delta.Error)) {
                        errors ??= new List<string>();
                        errors.Add(delta.Error);
                    }
                    break;
                case CompletionChunkKind.TokenUsage:
                    FlushPendingText();
                    tokenUsage = delta.TokenUsage;
                    break;
                default:
                    FlushPendingText();
                    break;
            }
        }

        FlushPendingText();

        if (blocks.Count == 0) {
            // 形状确定性兜底：下游始终能拿到至少一个块。
            blocks.Add(new ActionBlock.Text(string.Empty));
        }

        var message = new ActionMessage(blocks);
        return new AggregatedAction(message, invocation, tokenUsage, errors);
    }
}
