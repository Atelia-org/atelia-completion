namespace Atelia.Completion.Abstractions;

/// <summary>
/// 描述一次 Completion 调用在协议层面是“正常完成”还是“半截/失败”。
/// 该信息不属于历史消息体本身，而是供上层决定是否可以把结果持久化。
/// </summary>
public sealed record CompletionTermination {
    public CompletionTermination(
        CompletionTerminationKind kind,
        string? providerReason = null,
        string? detail = null
    ) {
        Kind = kind;
        ProviderReason = providerReason;
        Detail = detail;
    }

    public CompletionTerminationKind Kind { get; }

    /// <summary>
    /// Provider 原始的 stop_reason / finish_reason / event name 等结束标记。
    /// </summary>
    public string? ProviderReason { get; }

    /// <summary>
    /// 框架侧补充的、更适合日志与诊断的人类可读说明。
    /// </summary>
    public string? Detail { get; }

    public bool IsSuccess => Kind == CompletionTerminationKind.Completed;

    public static CompletionTermination Completed(string? providerReason = null, string? detail = null)
        => new(CompletionTerminationKind.Completed, providerReason, detail);

    public static CompletionTermination Incomplete(string? providerReason = null, string? detail = null)
        => new(CompletionTerminationKind.Incomplete, providerReason, detail);

    public static CompletionTermination Failed(string? providerReason = null, string? detail = null)
        => new(CompletionTerminationKind.Failed, providerReason, detail);
}

public enum CompletionTerminationKind {
    Completed,
    Incomplete,
    Failed
}
