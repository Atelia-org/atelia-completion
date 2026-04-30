using System.Threading;
using System.Threading.Tasks;

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
    /// 请求模型进行补全，内部完成流式消费与聚合，返回完整的 <see cref="CompletionResult"/>。
    /// 请求参数遵循为强化学习（RL）驱动的 Agent 设计的历史结构，方法内部负责将其转换为特定提供商所需的负载（Payload）。
    /// <para>
    /// <see cref="CompletionDescriptor"/> 由实现类根据 <see cref="Name"/>、<see cref="ApiSpecId"/> 和
    /// <paramref name="request"/>.<see cref="CompletionRequest.ModelId"/> 自行构造，无需外部额外传入。
    /// </para>
    /// </summary>
    /// <param name="request">包含历史记录、策略偏好和采样配置的补全请求。</param>
    /// <param name="observer">流式观察者，用于 UI 渐进显示、早停等。不需要观察时传 <see langword="null"/>。</param>
    /// <param name="cancellationToken">用于取消长时间流式推理操作的信号。</param>
    /// <returns>聚合后的完整结果快照，其 <see cref="CompletionResult.Message"/> 可直接作为下一轮历史的 <see cref="IActionMessage"/> 回灌。</returns>
    Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default);
}

/// <summary>
/// 流式补全的观察者，用于 UI 渐进显示、早停、流式日志等场景。
/// </summary>
/// <remarks>
/// <para><b>使用模式：</b></para>
/// <list type="bullet">
/// <item><b>事件</b>：直接订阅 event，适合轻量 lambda 或简单状态累积。</item>
/// <item><b>早停</b>：在回调中将 <see cref="ShouldStop"/> 设为 <see langword="true"/>，
/// 客户端会在下一检查点终止流处理。已聚合的部分结果仍通过 <see cref="CompletionResult"/> 返回。</item>
/// </list>
/// <para><b>关于 Thinking 与 Reasoning 的术语：</b>
/// 本项目以 <b>Thinking</b> 表示动作/状态（thinking 块的开始与结束），
/// 以 <b>Reasoning</b> 表示内容（模型在 thinking 块内产出的推理文本）。
/// 当前业界对此混用普遍（如 Anthropic 协议内称 thinking，OpenAI 称 reasoning），
/// 但本类保持上述区分以提升表达精度。</para>
/// </remarks>
public sealed class CompletionStreamObserver {
    /// <summary>
    /// 设为 <see langword="true"/> 以请求提前终止流处理。
    /// 已聚合的部分结果仍会通过 <see cref="CompletionResult"/> 返回。
    /// 一旦设为 <see langword="true"/> 不会自动复位（单向累积语义）。
    /// </summary>
    public bool ShouldStop { get; set; }

    // ── 文本增量 ──

    /// <summary>收到一段文本增量时触发。</summary>
    public event Action<string>? ReceivedTextDelta;

    /// <inheritdoc cref="ReceivedTextDelta"/>
    /// <remarks>默认实现仅负责触发 <see cref="ReceivedTextDelta"/>。</remarks>
    public void OnTextDelta(string delta) {
        ReceivedTextDelta?.Invoke(delta);
    }

    // ── 推理增量（仅明文）──

    /// <summary>
    /// 收到一段明文推理（reasoning）文本增量时触发。
    /// </summary>
    /// <remarks>
    /// <b>仅适用于明文 reasoning 场景</b>（如 Anthropic 的 thinking 块在请求中启用明文输出时）。
    /// 当 thinking 内容为加密/签名后的不透明负载时，<b>不会</b>触发此事件——
    /// 此时应依赖 <see cref="OnThinkingBegin"/> / <see cref="OnThinkingEnd"/> 获取基本状态提示。
    /// </remarks>
    public event Action<string>? ReceivedReasoningDelta;

    /// <inheritdoc cref="ReceivedReasoningDelta"/>
    /// <remarks>默认实现仅负责触发 <see cref="ReceivedReasoningDelta"/>。</remarks>
    public void OnReasoningDelta(string delta) {
        ReceivedReasoningDelta?.Invoke(delta);
    }

    // ── Thinking 生命周期 ──

    /// <summary>
    /// thinking 块开始时触发。无论 reasoning 内容是明文还是加密，此事件均会触发。
    /// 可用于显示"思考中…"等 UI 状态提示。
    /// </summary>
    public event Action? ReceivedThinkingBegin;

    /// <inheritdoc cref="ReceivedThinkingBegin"/>
    /// <remarks>默认实现仅负责触发 <see cref="ReceivedThinkingBegin"/>。</remarks>
    public void OnThinkingBegin() {
        ReceivedThinkingBegin?.Invoke();
    }

    /// <summary>
    /// thinking 块结束时触发。无论 reasoning 内容是明文还是加密，此事件均会触发。
    /// 可用于隐藏"思考中…"等 UI 状态提示。
    /// </summary>
    public event Action? ReceivedThinkingEnd;

    /// <inheritdoc cref="ReceivedThinkingEnd"/>
    /// <remarks>默认实现仅负责触发 <see cref="ReceivedThinkingEnd"/>。</remarks>
    public void OnThinkingEnd() {
        ReceivedThinkingEnd?.Invoke();
    }

    // ── 工具调用 ──

    /// <summary>一个完整的工具调用被解析完成时触发。</summary>
    public event Action<ParsedToolCall>? ReceivedToolCall;

    /// <inheritdoc cref="ReceivedToolCall"/>
    /// <remarks>默认实现仅负责触发 <see cref="ReceivedToolCall"/>。</remarks>
    public void OnToolCall(ParsedToolCall toolCall) {
        ReceivedToolCall?.Invoke(toolCall);
    }

}
