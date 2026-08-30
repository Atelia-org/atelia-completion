using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atelia.Completion.Gemini;

internal static class GeminiReplayPayloadCodec {
    internal sealed record GeminiReplayPayload(
        string Role,
        IReadOnlyList<GeminiReplayPayloadPart> Parts
    );

    internal sealed record GeminiReplayPayloadPart(
        string? Text,
        string? ThoughtSignature,
        GeminiReplayPayloadFunctionCall? FunctionCall
    );

    internal sealed record GeminiReplayPayloadFunctionCall(
        string Name,
        string ToolCallId,
        string RawArgumentsJson
    );

    internal static ReadOnlyMemory<byte> Encode(string role, IReadOnlyList<GeminiReplayPayloadPart> parts) {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(parts);

        var payload = new JsonObject {
            ["role"] = role,
            ["parts"] = new JsonArray(
                parts.Select(
                    static part => {
                        var partObject = new JsonObject();

                        if (part.Text is not null) {
                            partObject["text"] = part.Text;
                        }

                        if (!string.IsNullOrWhiteSpace(part.ThoughtSignature)) {
                            partObject["thoughtSignature"] = part.ThoughtSignature;
                        }

                        if (part.FunctionCall is not null) {
                            var functionCallObject = new JsonObject {
                                ["name"] = part.FunctionCall.Name,
                                ["id"] = part.FunctionCall.ToolCallId
                            };

                            try {
                                functionCallObject["args"] = JsonNode.Parse(part.FunctionCall.RawArgumentsJson);
                            }
                            catch (JsonException) {
                                functionCallObject["args"] = new JsonObject();
                            }

                            partObject["functionCall"] = functionCallObject;
                        }

                        return (JsonNode)partObject;
                    }
                ).ToArray()
            )
        };

        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    internal static GeminiReplayPayload Decode(ReadOnlyMemory<byte> payload) {
        try {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object) { throw new InvalidOperationException($"Expected JSON object but got {root.ValueKind}."); }

            if (!root.TryGetProperty("role", out var roleElement) || roleElement.ValueKind is not JsonValueKind.String) { throw new InvalidOperationException("Missing required string property 'role'."); }

            if (!root.TryGetProperty("parts", out var partsElement) || partsElement.ValueKind is not JsonValueKind.Array) { throw new InvalidOperationException("Missing required array property 'parts'."); }

            var parts = new List<GeminiReplayPayloadPart>();
            foreach (var partElement in partsElement.EnumerateArray()) {
                if (partElement.ValueKind is not JsonValueKind.Object) { throw new InvalidOperationException("Replay payload part must be a JSON object."); }

                string? text = null;
                if (partElement.TryGetProperty("text", out var textElement)) {
                    if (textElement.ValueKind is not JsonValueKind.String) { throw new InvalidOperationException("Replay payload property 'text' must be a string."); }

                    text = textElement.GetString();
                }

                string? thoughtSignature = null;
                if (partElement.TryGetProperty("thoughtSignature", out var thoughtSignatureElement)) {
                    if (thoughtSignatureElement.ValueKind is not JsonValueKind.String) { throw new InvalidOperationException("Replay payload property 'thoughtSignature' must be a string."); }

                    thoughtSignature = thoughtSignatureElement.GetString();
                }

                GeminiReplayPayloadFunctionCall? functionCall = null;
                if (partElement.TryGetProperty("functionCall", out var functionCallElement)) {
                    if (functionCallElement.ValueKind is not JsonValueKind.Object) { throw new InvalidOperationException("Replay payload property 'functionCall' must be an object."); }

                    var name = functionCallElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind is JsonValueKind.String
                        ? nameElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(name)) { throw new InvalidOperationException("Replay payload functionCall is missing required string property 'name'."); }

                    var toolCallId = functionCallElement.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.String
                        ? idElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(toolCallId)) { throw new InvalidOperationException("Replay payload functionCall is missing required string property 'id'."); }

                    var rawArgumentsJson = functionCallElement.TryGetProperty("args", out var argsElement)
                        ? argsElement.GetRawText()
                        : "{}";

                    functionCall = new GeminiReplayPayloadFunctionCall(name!, toolCallId!, rawArgumentsJson);
                }

                parts.Add(new GeminiReplayPayloadPart(text, thoughtSignature, functionCall));
            }

            return new GeminiReplayPayload(roleElement.GetString()!, parts);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) {
            throw new InvalidOperationException(
                $"Failed to deserialize Gemini replay payload for replay: {ex.Message}",
                ex
            );
        }
    }
}
