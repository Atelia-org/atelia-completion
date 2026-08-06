using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atelia.Completion.Anthropic;

internal static class AnthropicThinkingPayloadCodec {
    public static ReadOnlyMemory<byte> Encode(string thinking, string signature) {
        ArgumentNullException.ThrowIfNull(thinking);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        var payloadObject = new JsonObject {
            ["type"] = "thinking",
            ["thinking"] = thinking,
            ["signature"] = signature
        };

        return JsonSerializer.SerializeToUtf8Bytes(payloadObject);
    }

    public static ReadOnlyMemory<byte> EncodeRedacted(string data) {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        var payloadObject = new JsonObject {
            ["type"] = "redacted_thinking",
            ["data"] = data
        };

        return JsonSerializer.SerializeToUtf8Bytes(payloadObject);
    }

    public static AnthropicContentBlock Decode(ReadOnlyMemory<byte> payload) {
        try {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object) { throw new InvalidOperationException($"Expected JSON object but got {root.ValueKind}."); }

            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            return type switch {
                "thinking" => DecodeThinking(root),
                "redacted_thinking" => DecodeRedactedThinking(root),
                _ => throw new InvalidOperationException(
                    $"Expected type='thinking' or 'redacted_thinking' but got '{type ?? "<null>"}'."
                )
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or DecoderFallbackException or ArgumentException) {
            throw new InvalidOperationException(
                $"Failed to deserialize Anthropic thinking block payload for replay: {ex.Message}",
                ex
            );
        }
    }

    public static AnthropicContentBlock DecodeAndValidatePlainText(
        ReadOnlyMemory<byte> payload,
        string? plainText
    ) {
        AnthropicContentBlock block = Decode(payload);
        string? expectedPlainText = block switch {
            AnthropicThinkingBlock thinking when thinking.Thinking.Length > 0 => thinking.Thinking,
            AnthropicThinkingBlock => null,
            AnthropicRedactedThinkingBlock => null,
            _ => throw new InvalidOperationException($"Unsupported Anthropic reasoning block '{block.GetType().Name}'.")
        };
        if (!string.Equals(expectedPlainText, plainText, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                "Anthropic reasoning PlainText does not match its authoritative replay payload."
            );
        }
        return block;
    }

    private static AnthropicThinkingBlock DecodeThinking(JsonElement root) {
        if (!root.TryGetProperty("thinking", out var thinkingElement) || thinkingElement.ValueKind is not JsonValueKind.String) { throw new InvalidOperationException("Missing required string property 'thinking'."); }

        if (!root.TryGetProperty("signature", out var signatureElement)
            || signatureElement.ValueKind is not JsonValueKind.String
            || string.IsNullOrWhiteSpace(signatureElement.GetString())) {
            throw new InvalidOperationException("Missing required non-empty string property 'signature'.");
        }

        return new AnthropicThinkingBlock {
            Thinking = thinkingElement.GetString() ?? string.Empty,
            Signature = signatureElement.GetString()!
        };
    }

    private static AnthropicRedactedThinkingBlock DecodeRedactedThinking(JsonElement root) {
        if (!root.TryGetProperty("data", out var dataElement)
            || dataElement.ValueKind is not JsonValueKind.String
            || string.IsNullOrWhiteSpace(dataElement.GetString())) {
            throw new InvalidOperationException("Missing required non-empty string property 'data'.");
        }

        return new AnthropicRedactedThinkingBlock {
            Data = dataElement.GetString() ?? string.Empty
        };
    }
}
