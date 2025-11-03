using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Diagnostics;
using Atelia.LlmProviders;
using Atelia.LlmProviders.Utils;

namespace Atelia.LlmProviders.Anthropic;

/// <summary>
/// 解析 Anthropic SSE 流式响应事件。
/// 事件类型：message_start, content_block_start, content_block_delta, content_block_stop, message_delta, message_stop
/// </summary>
internal sealed class AnthropicStreamParser {
    private const string DebugCategory = "Provider";

    private readonly Dictionary<string, ToolDefinition> _toolDefinitions;
    private readonly Dictionary<int, ContentBlockState> _contentBlocks = new();
    private readonly List<ToolCallRequest> _toolCalls = new();
    private TokenUsage? _usage;

    public AnthropicStreamParser()
        : this(ImmutableArray<ToolDefinition>.Empty) {
    }

    public AnthropicStreamParser(ImmutableArray<ToolDefinition> toolDefinitions) {
        _toolDefinitions = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);

        if (!toolDefinitions.IsDefaultOrEmpty) {
            foreach (var definition in toolDefinitions) {
                if (_toolDefinitions.ContainsKey(definition.Name)) {
                    DebugUtil.Print(DebugCategory, $"[Anthropic] Duplicate tool definition ignored name={definition.Name}");
                    continue;
                }

                _toolDefinitions[definition.Name] = definition;
            }
        }
    }

    public IEnumerable<ModelOutputDelta> ParseEvent(string json) {
        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex) {
            DebugUtil.Print(DebugCategory, $"[Anthropic] Failed to parse event: {ex.Message}");
            yield break;
        }

        if (node is not JsonObject obj) { yield break; }

        var eventType = obj["type"]?.GetValue<string>();
        if (eventType is null) { yield break; }

        foreach (var delta in eventType switch {
            "message_start" => HandleMessageStart(obj),
            "content_block_start" => HandleContentBlockStart(obj),
            "content_block_delta" => HandleContentBlockDelta(obj),
            "content_block_stop" => HandleContentBlockStop(obj),
            "message_delta" => HandleMessageDelta(obj),
            "message_stop" => HandleMessageStop(obj),
            "ping" => Enumerable.Empty<ModelOutputDelta>(),
            "error" => HandleError(obj),
            _ => HandleUnknownEvent(eventType)
        }) {
            yield return delta;
        }
    }

    public TokenUsage? GetFinalUsage() => _usage;

    private IEnumerable<ModelOutputDelta> HandleMessageStart(JsonObject obj) {
        // message_start 包含初始 usage
        if (obj["message"]?["usage"] is JsonObject usage) {
            UpdateUsage(usage);
        }

        yield break;
    }

    private IEnumerable<ModelOutputDelta> HandleContentBlockStart(JsonObject obj) {
        var index = obj["index"]?.GetValue<int>() ?? -1;
        if (index < 0) { yield break; }

        var contentBlock = obj["content_block"] as JsonObject;
        if (contentBlock is null) { yield break; }

        var blockType = contentBlock["type"]?.GetValue<string>();

        var state = new ContentBlockState {
            Index = index,
            Type = blockType ?? "unknown"
        };

        if (blockType == "tool_use") {
            state.ToolUseId = contentBlock["id"]?.GetValue<string>() ?? string.Empty;
            state.ToolName = contentBlock["name"]?.GetValue<string>() ?? string.Empty;
        }

        _contentBlocks[index] = state;
        yield break;
    }

    private IEnumerable<ModelOutputDelta> HandleContentBlockDelta(JsonObject obj) {
        var index = obj["index"]?.GetValue<int>() ?? -1;
        if (index < 0 || !_contentBlocks.TryGetValue(index, out var state)) { yield break; }

        var delta = obj["delta"] as JsonObject;
        if (delta is null) { yield break; }

        var deltaType = delta["type"]?.GetValue<string>();

        if (deltaType == "text_delta") {
            var text = delta["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text)) {
                yield return ModelOutputDelta.Content(text);
            }
        }
        else if (deltaType == "input_json_delta") {
            var partial = delta["partial_json"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(partial)) {
                state.ToolInputJson += partial;
            }
        }
    }

    private IEnumerable<ModelOutputDelta> HandleContentBlockStop(JsonObject obj) {
        var index = obj["index"]?.GetValue<int>() ?? -1;
        if (index < 0 || !_contentBlocks.TryGetValue(index, out var state)) { yield break; }

        // 如果是工具调用块，生成 ToolCallRequest
        if (state.Type == "tool_use") {
            var toolCall = CreateToolCallRequest(state);
            _toolCalls.Add(toolCall);
            yield return ModelOutputDelta.ToolCall(toolCall);
        }
    }

    private IEnumerable<ModelOutputDelta> HandleMessageDelta(JsonObject obj) {
        // message_delta 包含 stop_reason 和增量 usage
        if (obj["usage"] is JsonObject usage) {
            UpdateUsage(usage);
        }

        yield break;
    }

    private IEnumerable<ModelOutputDelta> HandleMessageStop(JsonObject obj) {
        // 消息结束，无额外处理
        yield break;
    }

    private IEnumerable<ModelOutputDelta> HandleError(JsonObject obj) {
        var error = obj["error"]?["message"]?.GetValue<string>() ?? "Unknown error";
        DebugUtil.Print(DebugCategory, $"[Anthropic] API error: {error}");
        yield return ModelOutputDelta.ExecutionError(error);
    }

    private IEnumerable<ModelOutputDelta> HandleUnknownEvent(string eventType) {
        DebugUtil.Print(DebugCategory, $"[Anthropic] Unknown event type: {eventType}");
        yield break;
    }

    private void UpdateUsage(JsonObject usage) {
        var inputTokens = usage["input_tokens"]?.GetValue<int>() ?? 0;
        var outputTokens = usage["output_tokens"]?.GetValue<int>() ?? 0;

        // Anthropic 的 cache_read_input_tokens 和 cache_creation_input_tokens
        var cacheReadTokens = usage["cache_read_input_tokens"]?.GetValue<int>();
        var cacheCreationTokens = usage["cache_creation_input_tokens"]?.GetValue<int>();
        var cachedTokens = (cacheReadTokens ?? 0) + (cacheCreationTokens ?? 0);

        _usage = new TokenUsage(
            PromptTokens: inputTokens,
            CompletionTokens: outputTokens,
            CachedPromptTokens: cachedTokens > 0 ? cachedTokens : null
        );
    }

    /// <summary>
    /// Builds a <see cref="ToolCallRequest"/> from a completed Anthropic tool content block.
    /// </summary>
    /// <remarks>
    /// Ensures both parsed arguments and their raw textual counterparts are captured so downstream providers can
    /// reconstruct the invocation even when type conversion fails.
    /// </remarks>
    private ToolCallRequest CreateToolCallRequest(ContentBlockState state) {
        var rawArgumentsText = string.IsNullOrWhiteSpace(state.ToolInputJson) ? "{}" : state.ToolInputJson;
        IReadOnlyDictionary<string, object?>? arguments = null;
        IReadOnlyDictionary<string, object?>? fallbackArguments = null;
        IReadOnlyDictionary<string, string>? rawArguments = null;
        ImmutableDictionary<string, string>? fallbackRawArguments = null;
        string? parseError = null;
        string? parseWarning = null;
        string? fallbackWarning = null;

        try {
            using var document = JsonDocument.Parse(rawArgumentsText);
            if (document.RootElement.ValueKind == JsonValueKind.Object) {
                var warnings = new List<string>();
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var rawBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var property in document.RootElement.EnumerateObject()) {
                    var childPath = property.Name;
                    rawBuilder[property.Name] = ExtractRawArgument(property.Value);
                    dict[property.Name] = ConvertJsonElement(property.Value, childPath, warnings);
                }

                fallbackArguments = dict;
                fallbackRawArguments = rawBuilder.ToImmutable();
                if (warnings.Count > 0) {
                    fallbackWarning = string.Join("; ", warnings);
                }
            }
            else {
                parseError = CombineMessages(parseError, $"Arguments must be a JSON object but was {document.RootElement.ValueKind}.");
            }
        }
        catch (JsonException ex) {
            parseError = CombineMessages(parseError, $"JSON parse failed: {ex.Message}");
        }

        if (_toolDefinitions.TryGetValue(state.ToolName, out var definition)) {
            var parsed = JsonArgumentParser.ParseArguments(definition.Parameters, rawArgumentsText);
            arguments = parsed.Arguments;
            rawArguments = parsed.RawArguments;
            parseError = CombineMessages(parseError, parsed.ParseError);
            parseWarning = CombineMessages(parseWarning, parsed.ParseWarning);
        }
        else {
            parseWarning = CombineMessages(parseWarning, "tool_definition_missing");
            arguments = fallbackArguments;
            rawArguments = fallbackRawArguments ?? rawArguments;
            parseWarning = CombineMessages(parseWarning, fallbackWarning);
        }

        rawArguments ??= fallbackRawArguments;

        return new ToolCallRequest(
            ToolName: state.ToolName,
            ToolCallId: state.ToolUseId,
            RawArguments: rawArguments,
            Arguments: arguments,
            ParseError: parseError,
            ParseWarning: parseWarning
        );
    }

    private static string ExtractRawArgument(JsonElement element) {
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

    private static string? CombineMessages(string? first, string? second) {
        if (string.IsNullOrWhiteSpace(first)) { return second; }
        if (string.IsNullOrWhiteSpace(second)) { return first; }
        return string.Concat(first, "; ", second);
    }

    private static object? ConvertJsonElement(JsonElement element, string path, List<string> warnings) {
        switch (element.ValueKind) {
            case JsonValueKind.Object: {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject()) {
                    var childPath = string.IsNullOrEmpty(path)
                        ? property.Name
                        : string.Join('.', path, property.Name);
                    dict[property.Name] = ConvertJsonElement(property.Value, childPath, warnings);
                }
                return dict;
            }
            case JsonValueKind.Array: {
                var list = new List<object?>();
                var index = 0;
                foreach (var item in element.EnumerateArray()) {
                    var childPath = string.IsNullOrEmpty(path)
                        ? $"[{index}]"
                        : $"{path}[{index}]";
                    list.Add(ConvertJsonElement(item, childPath, warnings));
                    index++;
                }
                return list;
            }
            case JsonValueKind.String: {
                var text = element.GetString();
                if (TryPromoteString(text, out var promoted, out var warning)) {
                    if (warning is not null) {
                        warnings.Add($"{path}: {warning}");
                    }
                    return promoted;
                }
                return text;
            }
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

    private static bool TryPromoteString(string? value, out object? promoted, out string? warning) {
        promoted = null;
        warning = null;

        if (string.IsNullOrWhiteSpace(value)) { return false; }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) {
            promoted = true;
            warning = "string literal converted to boolean true";
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) {
            promoted = false;
            warning = "string literal converted to boolean false";
            return true;
        }

        if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)) {
            promoted = null;
            warning = "string literal converted to null";
            return true;
        }

        return false;
    }

    private sealed class ContentBlockState {
        public int Index { get; set; }
        public string Type { get; set; } = string.Empty;
        public string ToolUseId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string ToolInputJson { get; set; } = string.Empty;
    }
}
