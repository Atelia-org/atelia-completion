using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

internal sealed class OpenAIChatStreamParser {
    private const string DebugCategory = "Provider";

    private readonly Dictionary<string, ToolDefinition> _toolDefinitions;
    private readonly OpenAIChatWhitespaceContentMode _whitespaceContentMode;
    private readonly Dictionary<int, ToolCallState> _toolCalls = new();
    private TokenUsage? _usage;

    public OpenAIChatStreamParser()
        : this(ImmutableArray<ToolDefinition>.Empty, OpenAIChatWhitespaceContentMode.Preserve) {
    }

    public OpenAIChatStreamParser(
        ImmutableArray<ToolDefinition> toolDefinitions,
        OpenAIChatWhitespaceContentMode whitespaceContentMode = OpenAIChatWhitespaceContentMode.Preserve
    ) {
        _toolDefinitions = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);
        _whitespaceContentMode = whitespaceContentMode;

        if (!toolDefinitions.IsDefaultOrEmpty) {
            foreach (var definition in toolDefinitions) {
                if (_toolDefinitions.ContainsKey(definition.Name)) {
                    DebugUtil.Warning(DebugCategory, $"[OpenAI] Duplicate tool definition ignored name={definition.Name}");
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
            DebugUtil.Warning(DebugCategory, $"[OpenAI] Failed to parse event: {ex.Message}", ex);
            yield break;
        }

        if (node is not JsonObject obj) { yield break; }

        if (obj["error"] is JsonObject error) {
            var errorMessage = error["message"]?.GetValue<string>() ?? "Unknown error";
            DebugUtil.Warning(DebugCategory, $"[OpenAI] API error: {errorMessage}");
            yield return CompletionChunk.FromError(errorMessage);
            yield break;
        }

        if (obj["usage"] is JsonObject usage) {
            UpdateUsage(usage);
        }

        if (obj["choices"] is not JsonArray choices) { yield break; }

        foreach (var choiceNode in choices) {
            if (choiceNode is not JsonObject choice) { continue; }

            foreach (var delta in HandleChoice(choice)) {
                yield return delta;
            }
        }
    }

    public IEnumerable<CompletionChunk> Complete() {
        foreach (var delta in FlushPendingToolCalls()) {
            yield return delta;
        }
    }

    public TokenUsage? GetFinalUsage() => _usage;

    private IEnumerable<CompletionChunk> HandleChoice(JsonObject choice) {
        if (choice["delta"] is JsonObject delta) {
            foreach (var chunk in HandleDelta(delta)) {
                yield return chunk;
            }
        }

        var finishReason = choice["finish_reason"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(finishReason)) {
            foreach (var chunk in FlushPendingToolCalls()) {
                yield return chunk;
            }
        }
    }

    private IEnumerable<CompletionChunk> HandleDelta(JsonObject delta) {
        var hasToolCallsInCurrentDelta = delta["tool_calls"] is JsonArray { Count: > 0 };
        var content = delta["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(content)) {
            if (!ShouldIgnoreContentDelta(content, hasToolCallsInCurrentDelta)) {
                yield return CompletionChunk.FromContent(content);
            }
        }

        if (delta["tool_calls"] is JsonArray toolCalls) {
            var fallbackIndex = 0;
            foreach (var toolCallNode in toolCalls) {
                if (toolCallNode is JsonObject toolCall) {
                    MergeToolCallDelta(toolCall, fallbackIndex);
                }

                fallbackIndex++;
            }
        }
    }

    private bool ShouldIgnoreContentDelta(string content, bool hasToolCallsInCurrentDelta) {
        if (_whitespaceContentMode is not OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls) {
            return false;
        }

        return (_toolCalls.Count > 0 || hasToolCallsInCurrentDelta) && string.IsNullOrWhiteSpace(content);
    }

    private void MergeToolCallDelta(JsonObject toolCall, int fallbackIndex) {
        var index = toolCall["index"]?.GetValue<int>() ?? fallbackIndex;
        if (!_toolCalls.TryGetValue(index, out var state)) {
            state = new ToolCallState(index);
            _toolCalls[index] = state;
        }

        var toolCallId = toolCall["id"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(toolCallId)) {
            state.ToolCallId = toolCallId;
        }

        // 上游 type 字段只会是 "function"，我们不读取也不重传，避免存一份永远不会被读的 dead state。

        if (toolCall["function"] is JsonObject function) {
            var toolName = function["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(toolName)) {
                state.ToolName = toolName;
            }

            var argumentsFragment = function["arguments"]?.GetValue<string>();
            if (argumentsFragment is not null) {
                state.ArgumentsBuilder.Append(argumentsFragment);
            }
        }
    }

    private IEnumerable<CompletionChunk> FlushPendingToolCalls() {
        if (_toolCalls.Count == 0) { yield break; }

        foreach (var index in _toolCalls.Keys.OrderBy(static key => key).ToArray()) {
            var state = _toolCalls[index];
            yield return CompletionChunk.FromToolCall(CreateToolCall(state));
        }

        _toolCalls.Clear();
    }

    private void UpdateUsage(JsonObject usage) {
        var promptTokens = usage["prompt_tokens"]?.GetValue<int>() ?? _usage?.PromptTokens ?? 0;
        var completionTokens = usage["completion_tokens"]?.GetValue<int>() ?? _usage?.CompletionTokens ?? 0;

        int? cachedPromptTokens = _usage?.CachedPromptTokens;
        if (usage["prompt_tokens_details"] is JsonObject promptTokenDetails) {
            cachedPromptTokens = promptTokenDetails["cached_tokens"]?.GetValue<int>() ?? cachedPromptTokens;
        }

        _usage = new TokenUsage(promptTokens, completionTokens, cachedPromptTokens);
    }

    private ParsedToolCall CreateToolCall(ToolCallState state) {
        var rawArgumentsText = state.ArgumentsBuilder.Length == 0
            ? "{}"
            : state.ArgumentsBuilder.ToString();

        var toolName = state.ToolName ?? string.Empty;
        var toolCallId = string.IsNullOrWhiteSpace(state.ToolCallId) ? $"openai-call-{state.Index}" : state.ToolCallId;

        // 主路径：模型调用了已注册的工具，由 schema 引导的解析器一次解析到位。
        if (_toolDefinitions.TryGetValue(toolName, out var definition)) {
            var parsed = JsonArgumentParser.ParseArguments(definition.Parameters, rawArgumentsText);
            return new ParsedToolCall(
                ToolName: toolName,
                ToolCallId: toolCallId,
                RawArguments: parsed.RawArguments,
                Arguments: parsed.Arguments,
                ParseError: parsed.ParseError,
                ParseWarning: parsed.ParseWarning
            );
        }

        // 罕见路径：模型调了未注册的工具。仍尽量把 raw 文本结构化以便诊断/回放，
        // 但不做任何类型猜测——没有 schema 时，把 "42" 当数字反而会污染诊断。
        return BuildToolCallWithoutSchema(toolName, toolCallId, rawArgumentsText);
    }

    private static ParsedToolCall BuildToolCallWithoutSchema(string toolName, string toolCallId, string rawArgumentsText) {
        IReadOnlyDictionary<string, object?>? arguments = null;
        IReadOnlyDictionary<string, string>? rawArguments = null;
        string? parseError = null;

        try {
            using var document = JsonDocument.Parse(rawArgumentsText);
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                parseError = $"Arguments must be a JSON object but was {document.RootElement.ValueKind}.";
            }
            else {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var rawBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.RootElement.EnumerateObject()) {
                    rawBuilder[property.Name] = ExtractRawArgument(property.Value);
                    dict[property.Name] = ConvertJsonElement(property.Value);
                }
                arguments = dict;
                rawArguments = rawBuilder.ToImmutable();
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

    private static string ExtractRawArgument(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
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
            default:
                return null;
        }
    }

    private sealed class ToolCallState {
        public ToolCallState(int index) {
            Index = index;
        }

        public int Index { get; }
        public string? ToolCallId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder ArgumentsBuilder { get; } = new();
    }
}
