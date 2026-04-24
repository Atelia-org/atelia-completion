using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;

namespace Atelia.Completion.Anthropic;

/// <summary>
/// 解析 Anthropic SSE 流式响应事件。
/// 事件类型：message_start, content_block_start, content_block_delta, content_block_stop, message_delta, message_stop
/// </summary>
internal sealed class AnthropicStreamParser {
    private const string DebugCategory = "Provider";

    private readonly Dictionary<string, ToolDefinition> _toolDefinitions;
    private readonly Dictionary<int, ContentBlockState> _contentBlocks = new();
    private int? _promptTokens;
    private int? _completionTokens;
    private int? _cacheReadInputTokens;
    private int? _cacheCreationInputTokens;

    public AnthropicStreamParser()
        : this(ImmutableArray<ToolDefinition>.Empty) {
    }

    public AnthropicStreamParser(ImmutableArray<ToolDefinition> toolDefinitions) {
        _toolDefinitions = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);

        if (!toolDefinitions.IsDefaultOrEmpty) {
            foreach (var definition in toolDefinitions) {
                if (_toolDefinitions.ContainsKey(definition.Name)) {
                    DebugUtil.Warning(DebugCategory, $"[Anthropic] Duplicate tool definition ignored name={definition.Name}");
                    continue;
                }

                _toolDefinitions[definition.Name] = definition;
            }
        }
    }

    public IEnumerable<CompletionChunk> ParseEvent(string json) {
        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex) {
            DebugUtil.Warning(DebugCategory, $"[Anthropic] Failed to parse event: {ex.Message}", ex);
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
            "ping" => Enumerable.Empty<CompletionChunk>(),
            "error" => HandleError(obj),
            _ => HandleUnknownEvent(eventType)
        }) {
            yield return delta;
        }
    }

    public TokenUsage? GetFinalUsage() {
        if (_promptTokens is null && _completionTokens is null && _cacheReadInputTokens is null && _cacheCreationInputTokens is null) {
            return null;
        }

        var cachedPromptTokens = (_cacheReadInputTokens ?? 0) + (_cacheCreationInputTokens ?? 0);

        return new TokenUsage(
            PromptTokens: _promptTokens ?? 0,
            CompletionTokens: _completionTokens ?? 0,
            CachedPromptTokens: cachedPromptTokens > 0 ? cachedPromptTokens : null
        );
    }

    private IEnumerable<CompletionChunk> HandleMessageStart(JsonObject obj) {
        // message_start 包含初始 usage
        if (obj["message"]?["usage"] is JsonObject usage) {
            UpdateUsage(usage);
        }

        yield break;
    }

    private IEnumerable<CompletionChunk> HandleContentBlockStart(JsonObject obj) {
        var index = obj["index"]?.GetValue<int>() ?? -1;
        if (index < 0) { yield break; }

        var contentBlock = obj["content_block"] as JsonObject;
        if (contentBlock is null) { yield break; }

        var blockType = contentBlock["type"]?.GetValue<string>();

        var state = new ContentBlockState {
            Type = blockType ?? "unknown"
        };

        if (blockType == "tool_use") {
            state.ToolUseId = contentBlock["id"]?.GetValue<string>() ?? string.Empty;
            state.ToolName = contentBlock["name"]?.GetValue<string>() ?? string.Empty;
        }
        else if (blockType == "thinking") {
            // 偶尔 content_block_start 已携带初始 thinking/signature 文本（尽管常见为空），
            // 一并预填，后续 thinking_delta / signature_delta 继续追加。
            var initialThinking = contentBlock["thinking"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(initialThinking)) { state.ThinkingTextBuilder.Append(initialThinking); }
            var initialSignature = contentBlock["signature"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(initialSignature)) { state.ThinkingSignatureBuilder.Append(initialSignature); }
        }

        _contentBlocks[index] = state;
        yield break;
    }

    private IEnumerable<CompletionChunk> HandleContentBlockDelta(JsonObject obj) {
        var index = obj["index"]?.GetValue<int>() ?? -1;
        if (index < 0 || !_contentBlocks.TryGetValue(index, out var state)) { yield break; }

        var delta = obj["delta"] as JsonObject;
        if (delta is null) { yield break; }

        var deltaType = delta["type"]?.GetValue<string>();

        if (deltaType == "text_delta") {
            var text = delta["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text)) {
                yield return CompletionChunk.FromContent(text);
            }
        }
        else if (deltaType == "input_json_delta") {
            var partial = delta["partial_json"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(partial)) {
                state.ToolInputJsonBuilder.Append(partial);
            }
        }
        else if (deltaType == "thinking_delta") {
            var thinkingText = delta["thinking"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(thinkingText)) {
                state.ThinkingTextBuilder.Append(thinkingText);
            }
        }
        else if (deltaType == "signature_delta") {
            var signature = delta["signature"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(signature)) {
                state.ThinkingSignatureBuilder.Append(signature);
            }
        }
    }

    private IEnumerable<CompletionChunk> HandleContentBlockStop(JsonObject obj) {
        var index = obj["index"]?.GetValue<int>() ?? -1;
        if (index < 0 || !_contentBlocks.TryGetValue(index, out var state)) { yield break; }

        // 如果是工具调用块，生成 ToolCallRequest
        if (state.Type == "tool_use") {
            var toolCall = CreateToolCallRequest(state);
            yield return CompletionChunk.FromToolCall(toolCall);
        }
        else if (state.Type == "thinking") {
            var thinkingText = state.ThinkingTextBuilder.ToString();
            var signature = state.ThinkingSignatureBuilder.ToString();
            var payloadBytes = AnthropicThinkingPayloadCodec.Encode(thinkingText, string.IsNullOrEmpty(signature) ? null : signature);

            yield return CompletionChunk.FromThinking(
                new ThinkingChunk(
                    OpaquePayload: payloadBytes,
                    PlainTextForDebug: string.IsNullOrEmpty(thinkingText) ? null : thinkingText
                )
            );
        }

        _contentBlocks.Remove(index);
    }

    private IEnumerable<CompletionChunk> HandleMessageDelta(JsonObject obj) {
        // message_delta 包含 stop_reason 和增量 usage
        if (obj["usage"] is JsonObject usage) {
            UpdateUsage(usage);
        }

        yield break;
    }

    private IEnumerable<CompletionChunk> HandleMessageStop(JsonObject obj) {
        // 消息结束，无额外处理
        yield break;
    }

    private IEnumerable<CompletionChunk> HandleError(JsonObject obj) {
        var error = obj["error"]?["message"]?.GetValue<string>() ?? "Unknown error";
        DebugUtil.Warning(DebugCategory, $"[Anthropic] API error: {error}");
        yield return CompletionChunk.FromError(error);
    }

    private IEnumerable<CompletionChunk> HandleUnknownEvent(string eventType) {
        DebugUtil.Warning(DebugCategory, $"[Anthropic] Unknown event type: {eventType}");
        yield break;
    }

    private void UpdateUsage(JsonObject usage) {
        if (TryGetInt32(usage, "input_tokens", out var inputTokens)) {
            _promptTokens = inputTokens;
        }

        if (TryGetInt32(usage, "output_tokens", out var outputTokens)) {
            _completionTokens = outputTokens;
        }

        if (TryGetInt32(usage, "cache_read_input_tokens", out var cacheReadTokens)) {
            _cacheReadInputTokens = cacheReadTokens;
        }

        if (TryGetInt32(usage, "cache_creation_input_tokens", out var cacheCreationTokens)) {
            _cacheCreationInputTokens = cacheCreationTokens;
        }
    }

    /// <summary>
    /// Builds a <see cref="ParsedToolCall"/> from a completed Anthropic tool content block.
    /// </summary>
    /// <remarks>
    /// Ensures both parsed arguments and their raw textual counterparts are captured so downstream providers can
    /// reconstruct the invocation even when type conversion fails.
    /// </remarks>
    private ParsedToolCall CreateToolCallRequest(ContentBlockState state) {
        var rawArgumentsText = state.ToolInputJsonBuilder.Length == 0
            ? "{}"
            : state.ToolInputJsonBuilder.ToString();

        if (_toolDefinitions.TryGetValue(state.ToolName, out var definition)) {
            var parsed = JsonArgumentParser.ParseArguments(definition.Parameters, rawArgumentsText);
            return new ParsedToolCall(
                ToolName: state.ToolName,
                ToolCallId: state.ToolUseId,
                RawArguments: parsed.RawArguments,
                Arguments: parsed.Arguments,
                ParseError: parsed.ParseError,
                ParseWarning: parsed.ParseWarning
            );
        }

        return BuildToolCallWithoutSchema(state.ToolName, state.ToolUseId, rawArgumentsText);
    }

    private static ParsedToolCall BuildToolCallWithoutSchema(string toolName, string toolCallId, string rawArgumentsText) {
        IReadOnlyDictionary<string, object?>? arguments = null;
        IReadOnlyDictionary<string, string>? rawArguments = null;
        string? parseError = null;
        string? parseWarning = "tool_definition_missing";

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

    private static bool TryGetInt32(JsonObject obj, string propertyName, out int value) {
        value = default;

        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null) {
            return false;
        }

        try {
            value = node.GetValue<int>();
            return true;
        }
        catch (FormatException) {
            return false;
        }
        catch (InvalidOperationException) {
            return false;
        }
    }

    private static object? ConvertJsonElement(JsonElement element) {
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

    private sealed class ContentBlockState {
        public string Type { get; set; } = string.Empty;
        public string ToolUseId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public StringBuilder ToolInputJsonBuilder { get; } = new();
        public StringBuilder ThinkingTextBuilder { get; } = new();
        public StringBuilder ThinkingSignatureBuilder { get; } = new();
    }
}
