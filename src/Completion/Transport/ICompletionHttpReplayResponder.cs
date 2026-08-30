using System.Net.Http;

namespace Atelia.Completion.Transport;

/// <summary>
/// replay 模式下供 transport pipeline 查询的请求视图。
/// </summary>
public sealed record CompletionHttpReplayRequest(
    string Method,
    string? RequestUri,
    string? RequestText
);

/// <summary>
/// 根据请求视图返回一个重放用的响应。
/// </summary>
public interface ICompletionHttpReplayResponder {
    HttpResponseMessage CreateResponse(CompletionHttpReplayRequest request);
}
