using System.Collections.Generic;
using System.Threading;

namespace Atelia.Completion.Abstractions;

public interface ICompletionClient {
    /// <summary>
    /// 用以区分多个实例。
    /// 对于使用网络访问的Provider，推荐返回目标端点的Uri.Host。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 描述客户端所实现的 API 规范。
    /// 这是一个开放的不透明的标识符。例如: "openai-chat-v1", "openai-responses", "anthropic-messages-v1"。
    /// </summary>
    string ApiSpecId { get; }
    IAsyncEnumerable<CompletionChunk> StreamCompletionAsync(CompletionRequest request, CancellationToken cancellationToken);
}
