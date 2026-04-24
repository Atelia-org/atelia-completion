namespace Atelia.Completion.Abstractions;

/// <summary>
/// 描述一次模型调用的来源信息，包括供应商、API 规范和具体的模型标识。
/// 这是 provider-neutral 的 invocation identity schema，可供 Agent 框架用于 Turn lock、
/// replay 兼容性判断，也可供 Completion 层与 converter 共享。
/// </summary>
/// <param name="ProviderId">服务提供商的内部标识符，例如 "OpenAI" 或 "Anthropic"。</param>
/// <param name="ApiSpecId">本次调用所遵循的 API 规范，例如 <c>openai-chat-v1</c>。</param>
/// <param name="Model">所使用的具体模型名称或版本号。</param>
public record CompletionDescriptor(
    string ProviderId,
    string ApiSpecId,
    string Model
);
