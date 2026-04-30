using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// 一次 LLM 调用的流式 chunk 被聚合后的中性快照。直接实现 <see cref="IActionMessage"/>，
/// 可作为 <c>CompletionRequest.Context</c> 的下一轮 action 历史回灌。
/// <para>
/// 这是 <see cref="ICompletionClient.StreamCompletionAsync"/> 的标准产出。
/// 上层框架（如 Agent.Core）若需要附加自身私有元数据（serial、token estimate 等），
/// 应当 <b>包装</b> 本类型，而不是替代或扩展。
/// </para>
/// </summary>
/// <param name="Message">Canonical action 消息体；<see cref="ActionMessage.Blocks"/> 已在构造时冻结。</param>
/// <param name="Invocation">本次调用的来源描述符；<see cref="ActionBlock.Thinking.Origin"/> 与之对齐。</param>
/// <param name="Errors">流中通过错误事件报告的错误文本；无错误时为 <see langword="null"/>。</param>
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
        IReadOnlyList<string>? errors = null
    ) {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
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

    /// <summary>流中通过错误事件报告的错误文本；无错误时为 <see langword="null"/>。</summary>
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
