using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Anthropic;

/// <summary>
/// Anthropic provider 专用的 reasoning 块。承载经由 <see cref="AnthropicThinkingPayloadCodec"/>
/// 编码的 provider-native 字节（含 thinking 文本与可选 signature），
/// 仅能由 Anthropic converter 反向回灌时解码。
/// </summary>
public sealed record AnthropicReasoningBlock
    : ActionBlock.ReasoningBlock {
    private readonly byte[] _opaquePayload;

    /// <summary>
    /// Creates an Anthropic-native reasoning block and takes an immutable
    /// snapshot of the caller-owned payload.
    /// </summary>
    public AnthropicReasoningBlock(
        ReadOnlyMemory<byte> OpaquePayload,
        CompletionDescriptor Origin,
        string? PlainText = null
    ) : base(Origin, PlainText) {
        _opaquePayload = OpaquePayload.ToArray();
    }

    /// <summary>Anthropic-native serialized bytes (JSON: type/thinking/signature).</summary>
    public ReadOnlyMemory<byte> OpaquePayload => _opaquePayload;

    public void Deconstruct(
        out ReadOnlyMemory<byte> opaquePayload,
        out CompletionDescriptor origin,
        out string? plainText
    ) {
        opaquePayload = OpaquePayload;
        origin = Origin;
        plainText = PlainText;
    }
}
