using System.Text;
using Xunit;

namespace Atelia.Completion.Anthropic.Tests;

public sealed class AnthropicThinkingPayloadCodecTests {
    [Fact]
    public void EncodeDecode_RoundTripsThinkingAndSignature() {
        var payload = AnthropicThinkingPayloadCodec.Encode("Let me think.", "sig-123");

        var block = AnthropicThinkingPayloadCodec.Decode(payload);

        Assert.Equal("Let me think.", block.Thinking);
        Assert.Equal("sig-123", block.Signature);
    }

    [Fact]
    public void Decode_InvalidPayloadFailsFast() {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicThinkingPayloadCodec.Decode(Encoding.UTF8.GetBytes("""{"type":"not-thinking"}"""))
        );

        Assert.Contains("Failed to deserialize Anthropic thinking block payload", exception.Message, StringComparison.Ordinal);
    }
}
