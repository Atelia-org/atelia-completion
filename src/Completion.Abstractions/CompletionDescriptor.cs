namespace Atelia.Completion.Abstractions;

/// <summary>
/// 描述一次模型调用的来源信息，包括供应商、API 规范和具体的模型标识。
/// 这是 provider-neutral 的 invocation identity schema，可供 Agent 框架用于 Turn lock、
/// replay 兼容性判断，也可供 Completion 层与 converter 共享。
/// </summary>
/// <param name="ProviderId">服务提供商的内部标识符，例如 "OpenAI" 或 "Anthropic"。</param>
/// <param name="ApiSpecId">本次调用所遵循的 API 规范，例如 <c>openai-chat-v1</c>。</param>
/// <param name="Model">所使用的具体模型名称或版本号。</param>
public sealed record CompletionDescriptor {
    public CompletionDescriptor(string providerId, string apiSpecId, string model) {
        ProviderId = RequireNonBlank(providerId, nameof(providerId));
        ApiSpecId = RequireNonBlank(apiSpecId, nameof(apiSpecId));
        Model = RequireNonBlank(model, nameof(model));
    }

    /// <summary>服务提供商的内部标识符，例如 "OpenAI" 或 "Anthropic"。</summary>
    public string ProviderId { get; }

    /// <summary>本次调用所遵循的 API 规范，例如 <c>openai-chat-v1</c>。</summary>
    public string ApiSpecId { get; }

    /// <summary>所使用的具体模型名称或版本号。</summary>
    public string Model { get; }

    /// <summary>
    /// 从 client 与 request 派生标准的 invocation identity。
    /// 调用方应优先使用此工厂，而不是手拼 provider/api/model 三元组。
    /// </summary>
    public static CompletionDescriptor From(ICompletionClient client, CompletionRequest request) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        return new CompletionDescriptor(client.Name, client.ApiSpecId, request.ModelId);
    }

    /// <summary>
    /// 从 client 与显式 modelId 派生标准的 invocation identity。
    /// 适用于 request 尚未构造完成、但已知本次调用模型时的场景。
    /// </summary>
    public static CompletionDescriptor From(ICompletionClient client, string modelId) {
        ArgumentNullException.ThrowIfNull(client);

        return new CompletionDescriptor(client.Name, client.ApiSpecId, modelId);
    }

    private static string RequireNonBlank(string value, string paramName) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException("Value must not be null, empty, or whitespace.", paramName);
        }

        return value;
    }
}
