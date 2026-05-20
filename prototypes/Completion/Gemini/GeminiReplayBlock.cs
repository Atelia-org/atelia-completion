using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Gemini;

/// <summary>
/// Gemini provider 专用的 replay 块。
/// 承载一次 Gemini model turn 的 provider-native content parts 快照，
/// 以便在下一轮请求中回灌 <c>thoughtSignature</c> 与 <c>functionCall</c> 等 Gemini 特有信息。
/// </summary>
/// <param name="OpaquePayload">Gemini-native 序列化字节。</param>
/// <param name="Origin">产生该 replay payload 的调用来源描述符。</param>
/// <param name="PlainTextForDebug">可选调试文本；通常为该 turn 中所有文本 part 的拼接。</param>
public sealed record GeminiReplayBlock(
    ReadOnlyMemory<byte> OpaquePayload,
    CompletionDescriptor Origin,
    string? PlainTextForDebug = null
) : ActionBlock.ReasoningBlock(Origin, PlainTextForDebug);
