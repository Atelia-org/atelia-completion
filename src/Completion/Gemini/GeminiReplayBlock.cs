using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Gemini;

/// <summary>
/// Gemini provider 专用的 replay 块。
/// 承载一次 Gemini model turn 的 provider-native content parts 快照，
/// 以便在下一轮请求中回灌 <c>thoughtSignature</c> 与 <c>functionCall</c> 等 Gemini 特有信息。
/// </summary>
public sealed record GeminiReplayBlock
    : ActionBlock.ReasoningBlock {
    private readonly byte[] _opaquePayload;

    /// <summary>
    /// Creates a Gemini-native replay block and takes an immutable snapshot of
    /// the caller-owned payload.
    /// </summary>
    public GeminiReplayBlock(
        ReadOnlyMemory<byte> OpaquePayload,
        CompletionDescriptor Origin,
        string? PlainText = null
    ) : base(Origin, PlainText) {
        _opaquePayload = OpaquePayload.ToArray();
    }

    /// <summary>Gemini-native serialized bytes.</summary>
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
