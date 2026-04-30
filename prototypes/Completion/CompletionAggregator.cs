using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion;

/// <summary>
/// 聚合 provider 流式输出的共享工具。
/// 供 <see cref="ICompletionClient"/> 实现者在内部循环中调用类型化方法增量喂入，
/// 最后调用 <see cref="Build"/> 产出完整快照。
/// </summary>
/// <remarks>
/// <para>
/// <b>契约</b>：
/// <list type="bullet">
/// <item>连续 <see cref="AppendContent"/> 片段合并为单个 <see cref="ActionBlock.Text"/>；
/// text/tool_call/thinking 边界处会切块以保留顺序。</item>
/// <item>每个 <see cref="AppendToolCall"/> 直接转为 <see cref="ActionBlock.ToolCall"/>。</item>
/// <item>每个 <see cref="AppendThinking"/> 被包装为 <see cref="ActionBlock.Thinking"/>，
/// <see cref="ActionBlock.Thinking.Origin"/> = 构造时传入的 <see cref="CompletionDescriptor"/>，
/// <see cref="ThinkingChunk.OpaquePayload"/> 原样透传，<b>不解析</b>。</item>
/// <item>所有 <see cref="AppendError"/> 文本被收集到 <see cref="AggregatedAction.Errors"/>，
/// <b>不抛异常</b>——调用方自行决定是否中断或上报。</item>
/// <item>若流末尾无任何块，会补一个空 <see cref="ActionBlock.Text"/> 以保证下游消费者的形状确定性。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class CompletionAggregator {
    private readonly CompletionDescriptor _invocation;
    private readonly List<ActionBlock> _blocks = new();
    private readonly StringBuilder _contentBuilder = new();
    private TokenUsage? _tokenUsage;
    private List<string>? _errors;

    public CompletionAggregator(CompletionDescriptor invocation) {
        _invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
    }

    /// <summary>
    /// 喂入一段文本内容。连续调用时片段自动合并；当后续调用 <see cref="AppendToolCall"/>、
    /// <see cref="AppendThinking"/>、<see cref="AppendError"/> 或 <see cref="AppendTokenUsage"/> 时会自动切块。
    /// </summary>
    public void AppendContent(string text) {
        if (!string.IsNullOrEmpty(text)) {
            _contentBuilder.Append(text);
        }
    }

    /// <summary>
    /// 喂入一个已完成的工具调用请求。会先 flush 待定文本再追加块。
    /// </summary>
    public void AppendToolCall(ParsedToolCall toolCall) {
        FlushPendingText();
        _blocks.Add(new ActionBlock.ToolCall(toolCall));
    }

    /// <summary>
    /// 喂入一个已完成的 thinking 块。
    /// <see cref="ThinkingChunk.OpaquePayload"/> 原样透传，<b>不解析</b>。
    /// </summary>
    public void AppendThinking(ThinkingChunk thinking) {
        FlushPendingText();
        _blocks.Add(new ActionBlock.Thinking(
            _invocation,
            thinking.OpaquePayload,
            thinking.PlainTextForDebug
        ));
    }

    /// <summary>
    /// 喂入一条错误文本。错误不会抛异常，仅被收集到 <see cref="AggregatedAction.Errors"/>。
    /// </summary>
    public void AppendError(string error) {
        FlushPendingText();
        if (!string.IsNullOrEmpty(error)) {
            _errors ??= new List<string>();
            _errors.Add(error);
        }
    }

    /// <summary>
    /// 喂入 token 用量统计。通常只在流末尾调用一次。
    /// </summary>
    public void AppendTokenUsage(TokenUsage usage) {
        FlushPendingText();
        _tokenUsage = usage;
    }

    /// <summary>
    /// 产出最终的 <see cref="AggregatedAction"/>。调用此方法后不应再调用 <see cref="Append"/>。
    /// </summary>
    public AggregatedAction Build() {
        FlushPendingText();

        if (_blocks.Count == 0) {
            // 形状确定性兜底：下游始终能拿到至少一个块。
            _blocks.Add(new ActionBlock.Text(string.Empty));
        }

        var message = new ActionMessage(_blocks);
        return new AggregatedAction(message, _invocation, _tokenUsage, _errors);
    }

    private void FlushPendingText() {
        if (_contentBuilder.Length == 0) { return; }
        _blocks.Add(new ActionBlock.Text(_contentBuilder.ToString()));
        _contentBuilder.Clear();
    }
}
