using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Anthropic;

/// <summary>
/// Anthropic provider 专用的 reasoning 块。承载经由 <see cref="AnthropicThinkingPayloadCodec"/>
/// 编码的 provider-native 字节（含 thinking 文本与可选 signature），
/// 仅能由 Anthropic converter 反向回灌时解码。
/// </summary>
/// <param name="OpaquePayload">Anthropic-native 序列化字节（JSON: type/thinking/signature）。</param>
/// <param name="Origin">产生该 reasoning 的调用来源描述符。</param>
/// <param name="PlainTextForDebug">可选明文，仅供日志/UI/调试使用。</param>
public sealed record AnthropicReasoningBlock(
    System.ReadOnlyMemory<byte> OpaquePayload,
    CompletionDescriptor Origin,
    string? PlainTextForDebug = null
) : ActionBlock.ReasoningBlock(Origin, PlainTextForDebug);
