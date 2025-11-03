using System.Collections.Generic;
using System.Threading;

namespace Atelia.LlmProviders;

public interface IProviderClient {
    /// <summary>
    /// 用以区分多个实例。
    /// 对于使用网络访问的Provider，推荐返回目标端点的Uri.Host。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 所使用的协议名称。例如"openai-v1"或"openai-responses"。
    /// </summary>
    string Specification { get; }
    IAsyncEnumerable<ModelOutputDelta> CallModelAsync(LlmRequest request, CancellationToken cancellationToken);
}
