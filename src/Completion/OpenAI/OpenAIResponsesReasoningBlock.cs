using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.OpenAI;

/// <summary>
/// OpenAI Responses provider 专用的 reasoning 块。
/// 保留 provider 原样 reasoning item JSON，便于后续以同源 payload 回灌。
/// </summary>
/// <param name="RawItemJson">上游返回的 reasoning item 原样 JSON。</param>
/// <param name="Origin">产生该 reasoning 的调用来源描述符。</param>
/// <param name="PlainText">可选明文（reasoning summary）；仅用于展示/日志，不参与回灌。</param>
public sealed record OpenAIResponsesReasoningBlock(
    string RawItemJson,
    CompletionDescriptor Origin,
    string? PlainText = null
) : ActionBlock.ReasoningBlock(Origin, PlainText) {
    internal void ValidatePlainText() {
        string? expected = ExtractPlainText(RawItemJson);
        if (!string.Equals(expected, PlainText, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                "OpenAI Responses reasoning PlainText does not match its authoritative reasoning item payload."
            );
        }
    }

    internal static string? ExtractPlainText(string rawItemJson) {
        try {
            using var document = JsonDocument.Parse(rawItemJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "reasoning", StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    "OpenAI Responses reasoning payload must be a reasoning item object."
                );
            }
            if (!root.TryGetProperty("summary", out var summary)
                || summary.ValueKind is JsonValueKind.Null) {
                return null;
            }
            if (summary.ValueKind is not JsonValueKind.Array) {
                throw new InvalidOperationException(
                    "OpenAI Responses reasoning payload summary must be an array or null."
                );
            }

            var builder = new StringBuilder();
            foreach (var entry in summary.EnumerateArray()) {
                if (entry.ValueKind is JsonValueKind.String) {
                    builder.Append(entry.GetString());
                    continue;
                }
                if (entry.ValueKind is JsonValueKind.Object
                    && entry.TryGetProperty("text", out var text)
                    && text.ValueKind is JsonValueKind.String) {
                    builder.Append(text.GetString());
                }
            }
            return builder.Length == 0 ? null : builder.ToString();
        }
        catch (JsonException ex) {
            throw new InvalidOperationException(
                "OpenAI Responses reasoning payload is not valid JSON.",
                ex
            );
        }
    }
}
