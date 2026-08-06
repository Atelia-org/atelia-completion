using System.Text;
using Xunit;

namespace Atelia.Completion.Anthropic.Tests;

public sealed class AnthropicThinkingPayloadCodecTests {
    [Fact]
    public void EncodeDecode_RoundTripsThinkingAndSignature() {
        var payload = AnthropicThinkingPayloadCodec.Encode("Let me think.", "sig-123");

        var block = Assert.IsType<AnthropicThinkingBlock>(AnthropicThinkingPayloadCodec.Decode(payload));

        Assert.Equal("Let me think.", block.Thinking);
        Assert.Equal("sig-123", block.Signature);
    }

    [Fact]
    public void EncodeDecode_RoundTripsRedactedThinking() {
        var payload = AnthropicThinkingPayloadCodec.EncodeRedacted("EncryptedBlob==");

        var block = Assert.IsType<AnthropicRedactedThinkingBlock>(AnthropicThinkingPayloadCodec.Decode(payload));

        Assert.Equal("EncryptedBlob==", block.Data);
    }

    [Fact]
    public void Decode_InvalidPayloadFailsFast() {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicThinkingPayloadCodec.Decode(Encoding.UTF8.GetBytes("""{"type":"not-thinking"}"""))
        );

        Assert.Contains("Failed to deserialize Anthropic thinking block payload", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"type\":\"thinking\",\"thinking\":\"reason\"}")]
    [InlineData("{\"type\":\"thinking\",\"thinking\":\"reason\",\"signature\":\" \"}")]
    [InlineData("{\"type\":\"redacted_thinking\",\"data\":\"\"}")]
    public void Decode_MissingReplayAuthorityFailsFast(string json) {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicThinkingPayloadCodec.Decode(Encoding.UTF8.GetBytes(json))
        );

        Assert.Contains("required non-empty string", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeAndValidatePlainText_RejectsDivergenceFromPayload() {
        var payload = AnthropicThinkingPayloadCodec.Encode("authoritative", "sig");

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicThinkingPayloadCodec.DecodeAndValidatePlainText(payload, "stale")
        );

        Assert.Contains("PlainText", exception.Message, StringComparison.Ordinal);
    }
}
