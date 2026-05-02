using Atelia.Completion.Abstractions;

namespace Atelia.Completion.OpenAI;

/// <summary>
/// OpenAI Chat compatible provider 专用的 reasoning 块。
/// 当前用于承载 <c>reasoning_content</c> 的原样文本，
/// 可在支持 replay 的 dialect 下回灌到 assistant message。
/// </summary>
/// <param name="Content">上游返回的 reasoning_content 原样文本。</param>
/// <param name="Origin">产生该 reasoning 的调用来源描述符。</param>
/// <param name="PlainTextForDebug">可选调试文本；默认与 <paramref name="Content"/> 相同。</param>
public sealed record OpenAIChatReasoningBlock(
    string Content,
    CompletionDescriptor Origin,
    string? PlainTextForDebug = null
) : ActionBlock.ReasoningBlock(Origin, PlainTextForDebug ?? Content);
