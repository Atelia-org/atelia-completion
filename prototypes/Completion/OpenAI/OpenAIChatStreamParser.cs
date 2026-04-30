using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

/// <summary>
/// 解析 OpenAI Chat SSE 流式响应事件，直接向 <see cref="CompletionAggregator"/> 喂入增量数据。
/// </summary>
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
        StreamParserToolUtility.LoadToolDefinitions(toolDefinitions, _toolDefinitions, "OpenAI");
    }

    public void ParseEvent(string json, CompletionAggregator aggregator) {
        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex) {
            DebugUtil.Warning(DebugCategory, $"[OpenAI] Failed to parse event: {ex.Message}", ex);
            return;
        }

        if (node is not JsonObject obj) { return; }

        if (obj["error"] is JsonObject error) {
            var errorMessage = error["message"]?.GetValue<string>() ?? "Unknown error";
            DebugUtil.Warning(DebugCategory, $"[OpenAI] API error: {errorMessage}");
            aggregator.AppendError(errorMessage);
            return;
        }

        if (obj["usage"] is JsonObject usage) {
            UpdateUsage(usage);
        }

        if (obj["choices"] is not JsonArray choices) { return; }

        foreach (var choiceNode in choices) {
            if (choiceNode is not JsonObject choice) { continue; }
            HandleChoice(choice, aggregator);
        }
    }

    public void Complete(CompletionAggregator aggregator) {
        FlushPendingToolCalls(aggregator);
    }

    public void DiscardIncompleteStreamingState() {
        _toolCalls.Clear();
    }

    public TokenUsage? GetFinalUsage() => _usage;

    private void HandleChoice(JsonObject choice, CompletionAggregator aggregator) {
        if (choice["delta"] is JsonObject delta) {
            HandleDelta(delta, aggregator);
        }

        var finishReason = choice["finish_reason"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(finishReason)) {
            FlushPendingToolCalls(aggregator);
        }
    }

    private void HandleDelta(JsonObject delta, CompletionAggregator aggregator) {
        var hasToolCallsInCurrentDelta = delta["tool_calls"] is JsonArray { Count: > 0 };
        var content = delta["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(content)) {
            if (!ShouldIgnoreContentDelta(content, hasToolCallsInCurrentDelta)) {
                aggregator.AppendContent(content);
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

    private void FlushPendingToolCalls(CompletionAggregator aggregator) {
        if (_toolCalls.Count == 0) { return; }

        foreach (var index in _toolCalls.Keys.OrderBy(static key => key).ToArray()) {
            var state = _toolCalls[index];
            aggregator.AppendToolCall(CreateToolCall(state));
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

    private static ParsedToolCall BuildToolCallWithoutSchema(string toolName, string toolCallId, string rawArgumentsText)
        => StreamParserToolUtility.BuildToolCallWithoutSchema(toolName, toolCallId, rawArgumentsText);

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
