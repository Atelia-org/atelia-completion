using System.Collections.Generic;
using System.Linq;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// 一次 LLM 调用的完整结果快照。承载消息体与调用元信息，是
/// <see cref="ICompletionClient.StreamCompletionAsync"/> 的标准产出。
/// <para>
/// <b>分层边界</b>：本类型是 Completion 调用的 envelope，<b>不</b>实现 <see cref="IHistoryMessage"/>。
/// 历史回灌请使用 <see cref="Message"/>（纯 <see cref="ActionMessage"/>），
/// 上层框架（如 Agent.Core）通常会进一步包装为持有 <see cref="ActionMessage"/> 的 ActionEntry。
/// </para>
/// <para>
/// 未来若确有需要，可继续扩展 Completion 级元信息；这些元信息仍不进入 <see cref="ActionMessage"/>。
/// </para>
/// </summary>
/// <param name="Message">Canonical action 消息体；<see cref="ActionMessage.Blocks"/> 已在构造时冻结。</param>
/// <param name="Invocation">本次调用的来源描述符；<see cref="ActionBlock.Thinking.Origin"/> 与之对齐。</param>
/// <param name="Errors">流中通过错误事件报告的错误文本；无错误时为 <see langword="null"/>。</param>
public sealed record CompletionResult {
    private ActionMessage _message = null!;
    private CompletionDescriptor _invocation = null!;
    private IReadOnlyList<string>? _errors;

    /// <summary>
    /// 创建 <see cref="CompletionResult"/>。
    /// </summary>
    public CompletionResult(
        ActionMessage message,
        CompletionDescriptor invocation,
        IReadOnlyList<string>? errors = null
    ) {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        Errors = errors;
    }

    /// <summary>Canonical action 消息体。历史回灌请使用此字段。</summary>
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

    private static IReadOnlyList<T> FreezeList<T>(IReadOnlyList<T> items)
        => items.Count == 0 ? Array.AsReadOnly(Array.Empty<T>()) : Array.AsReadOnly(items.ToArray());
}
