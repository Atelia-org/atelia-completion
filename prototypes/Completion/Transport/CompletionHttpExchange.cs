namespace Atelia.Completion.Transport;

/// <summary>
/// 一次 HTTP 文本交换的最小快照。
/// </summary>
/// <remarks>
/// <para>
/// 当前 MVP 只记录 text-first 视图，服务于调试日志、golden log 与 mock replay。
/// </para>
/// <para>
/// <see cref="ResponseText"/> 记录的是调用方实际消费到的文本；若上层提前停止读取流，
/// 这里允许是部分响应。
/// </para>
/// <para>
/// 如果请求在 transport 层于拿到 <see cref="System.Net.Http.HttpResponseMessage"/> 之前失败，
/// 则 <see cref="StatusCode"/> 与 <see cref="ResponseText"/> 允许为 null，并改由
/// <see cref="ErrorText"/> 记录失败摘要。
/// </para>
/// </remarks>
public sealed record CompletionHttpExchange(
    string Method,
    string? RequestUri,
    string? RequestText,
    int? StatusCode,
    string? ResponseText,
    string? ErrorText
);
