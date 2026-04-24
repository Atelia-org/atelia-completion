using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atelia.Completion.Anthropic;

internal static class AnthropicThinkingPayloadCodec {
    public static ReadOnlyMemory<byte> Encode(string thinking, string? signature) {
        var payloadObject = new JsonObject {
            ["type"] = "thinking",
            ["thinking"] = thinking ?? string.Empty
        };

        if (!string.IsNullOrEmpty(signature)) {
            payloadObject["signature"] = signature;
        }

        return JsonSerializer.SerializeToUtf8Bytes(payloadObject);
    }

    public static AnthropicThinkingBlock Decode(ReadOnlyMemory<byte> payload) {
        try {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object) {
                throw new InvalidOperationException($"Expected JSON object but got {root.ValueKind}.");
            }

            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            if (!string.Equals(type, "thinking", StringComparison.Ordinal)) {
                throw new InvalidOperationException($"Expected type='thinking' but got '{type ?? "<null>"}'.");
            }

            if (!root.TryGetProperty("thinking", out var thinkingElement) || thinkingElement.ValueKind is not JsonValueKind.String) {
                throw new InvalidOperationException("Missing required string property 'thinking'.");
            }

            string? signature = null;
            if (root.TryGetProperty("signature", out var signatureElement)) {
                if (signatureElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) {
                    throw new InvalidOperationException($"Property 'signature' must be string or null but was {signatureElement.ValueKind}.");
                }
                signature = signatureElement.ValueKind == JsonValueKind.String
                    ? signatureElement.GetString()
                    : null;
            }

            return new AnthropicThinkingBlock {
                Thinking = thinkingElement.GetString() ?? string.Empty,
                Signature = string.IsNullOrEmpty(signature) ? null : signature
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or DecoderFallbackException or ArgumentException) {
            throw new InvalidOperationException(
                $"Failed to deserialize Anthropic thinking block payload for replay: {ex.Message}",
                ex
            );
        }
    }
}
