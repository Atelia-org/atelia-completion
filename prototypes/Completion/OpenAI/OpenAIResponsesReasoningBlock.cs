using Atelia.Completion.Abstractions;

namespace Atelia.Completion.OpenAI;

/// <summary>
/// OpenAI Responses provider 专用的 reasoning 块。
/// 保留 provider 原样 reasoning item JSON，便于后续以同源 payload 回灌。
/// </summary>
/// <param name="RawItemJson">上游返回的 reasoning item 原样 JSON。</param>
/// <param name="Origin">产生该 reasoning 的调用来源描述符。</param>
/// <param name="PlainTextForDebug">可选调试文本；仅用于日志/UI 展示，不参与回灌。</param>
public sealed record OpenAIResponsesReasoningBlock(
    string RawItemJson,
    CompletionDescriptor Origin,
    string? PlainTextForDebug = null
) : ActionBlock.ReasoningBlock(Origin, PlainTextForDebug);
