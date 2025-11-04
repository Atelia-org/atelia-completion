using System.Collections.Generic;
using System.Threading;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// 为不同模型提供商的补全（Completion）客户端提供一个抽象层。
/// 在强化学习（RL）语境下，它扮演"策略近似器"或"价值近似器"的角色，同时确保对外提供统一的历史和动作接口。
/// </summary>
public interface ICompletionClient {
    /// <summary>
    /// 获取一个用以区分多个客户端实例的唯一名称。
    /// 对于通过网络访问的提供商，推荐返回其服务终结点的 Uri.Host。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 获取描述此客户端所实现的 API 规范的标识符。
    /// 这是一个开放的不透明标识符，例如 "openai-chat-v1"、"openai-responses" 或 "anthropic-messages-v1"。
    /// 框架可依据此标识调整历史记录的映射策略，以适应不同的 API 范式。
    /// </summary>
    string ApiSpecId { get; }

    /// <summary>
    /// 以流式方式请求模型进行补全。
    /// 请求参数遵循为强化学习（RL）驱动的 Agent 设计的历史结构，方法内部负责将其转换为特定提供商所需的负载（Payload）。
    /// </summary>
    /// <param name="request">包含历史记录、策略偏好和采样配置的补全请求。</param>
    /// <param name="cancellationToken">用于取消长时间流式推理操作的信号。</param>
    /// <returns>一个异步枚举，按块返回补全结果，以便实时更新 Agent 的动作或价值评估。</returns>
    IAsyncEnumerable<CompletionChunk> StreamCompletionAsync(CompletionRequest request, CancellationToken cancellationToken);
}
