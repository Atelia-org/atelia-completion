using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Gemini;

/// <summary>
/// Gemini provider 专用的 replay 块。
/// 承载一次 Gemini model turn 的 provider-native content parts 快照，
/// 以便在下一轮请求中回灌 <c>thoughtSignature</c> 与 <c>functionCall</c> 等 Gemini 特有信息。
/// </summary>
/// <param name="OpaquePayload">Gemini-native 序列化字节。</param>
/// <param name="Origin">产生该 replay payload 的调用来源描述符。</param>
/// <param name="PlainText">可选的真实明文 reasoning。当前 Gemini parser 只保存 replay metadata，因此为 <see langword="null"/>。</param>
public sealed record GeminiReplayBlock(
    ReadOnlyMemory<byte> OpaquePayload,
    CompletionDescriptor Origin,
    string? PlainText = null
) : ActionBlock.ReasoningBlock(Origin, PlainText);
