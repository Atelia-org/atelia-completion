using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Completion.Utils;

/// <summary>
/// StreamParser 共享静态工具：跨 provider 的 JSON 参数解析 / 工具定义加载 / 类型转换。
/// </summary>
internal static class StreamParserToolUtility {
    /// <summary>
    /// 将 <see cref="ImmutableArray{ToolDefinition}"/> 加载到 <paramref name="target"/> 字典中。
    /// 重复项通过 <see cref="DebugUtil.Warning"/> 记录并跳过。
    /// </summary>
    public static void LoadToolDefinitions(
        ImmutableArray<ToolDefinition> toolDefinitions,
        Dictionary<string, ToolDefinition> target,
        string debugPrefix
    ) {
        if (toolDefinitions.IsDefaultOrEmpty) { return; }

        foreach (var definition in toolDefinitions) {
            if (target.ContainsKey(definition.Name)) {
                DebugUtil.Warning("Provider", $"[{debugPrefix}] Duplicate tool definition ignored name={definition.Name}");
                continue;
            }

            target[definition.Name] = definition;
        }
    }

    /// <summary>
    /// 无 schema 回退：将 raw JSON arguments 文本解析为 <see cref="ParsedToolCall"/>，
    /// 不做类型猜测。
    /// </summary>
    public static ParsedToolCall BuildToolCallWithoutSchema(string toolName, string toolCallId, string rawArgumentsText) {
        IReadOnlyDictionary<string, object?>? arguments = null;
        IReadOnlyDictionary<string, string>? rawArguments = null;
        string? parseError = null;

        try {
            using var document = JsonDocument.Parse(rawArgumentsText);
            if (document.RootElement.ValueKind == JsonValueKind.Object) {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var rawBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var property in document.RootElement.EnumerateObject()) {
                    rawBuilder[property.Name] = ExtractRawArgument(property.Value);
                    dict[property.Name] = ConvertJsonElement(property.Value);
                }

                arguments = dict;
                rawArguments = rawBuilder.ToImmutable();
            }
            else {
                parseError = $"Arguments must be a JSON object but was {document.RootElement.ValueKind}.";
            }
        }
        catch (JsonException ex) {
            parseError = $"JSON parse failed: {ex.Message}";
        }

        return new ParsedToolCall(
            ToolName: toolName,
            ToolCallId: toolCallId,
            RawArguments: rawArguments,
            Arguments: arguments,
            ParseError: parseError,
            ParseWarning: "tool_definition_missing"
        );
    }

    /// <summary>
    /// 将 <see cref="JsonElement"/> 的值转为保留原始文本形态的字符串。
    /// </summary>
    public static string ExtractRawArgument(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Object => element.GetRawText(),
            JsonValueKind.Array => element.GetRawText(),
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// 将 <see cref="JsonElement"/> 递归转换为 CLR 对象（<c>Dictionary</c>、<c>List</c>、基础类型）。
    /// </summary>
    public static object? ConvertJsonElement(JsonElement element) {
        switch (element.ValueKind) {
            case JsonValueKind.Object: {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject()) {
                    dict[property.Name] = ConvertJsonElement(property.Value);
                }
                return dict;
            }
            case JsonValueKind.Array: {
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray()) {
                    list.Add(ConvertJsonElement(item));
                }
                return list;
            }
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var longValue)) { return longValue; }
                if (element.TryGetDouble(out var doubleValue)) { return doubleValue; }
                return element.GetDecimal();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.GetBoolean();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return element.ToString();
        }
    }
}
