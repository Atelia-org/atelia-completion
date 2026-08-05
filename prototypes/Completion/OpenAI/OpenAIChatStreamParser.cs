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

    private readonly OpenAIChatWhitespaceContentMode _whitespaceContentMode;
    private readonly OpenAIChatReasoningMode _reasoningMode;
    private readonly Dictionary<int, ToolCallState> _toolCalls = new();
    private readonly StringBuilder _reasoningContentBuilder = new();
    private bool _terminalEventObserved;
    private bool _reasoningInProgress;

    public bool TerminalEventObserved => _terminalEventObserved;

    public OpenAIChatStreamParser(
        OpenAIChatWhitespaceContentMode whitespaceContentMode = OpenAIChatWhitespaceContentMode.Preserve,
        OpenAIChatReasoningMode reasoningMode = OpenAIChatReasoningMode.Ignore
    ) {
        _whitespaceContentMode = whitespaceContentMode;
        _reasoningMode = reasoningMode;
    }

    public void ParseEvent(string json, CompletionAggregator aggregator) {
        if (_terminalEventObserved) { return; }

        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex) {
            throw new InvalidDataException("OpenAI chat stream contained malformed provider JSON.", ex);
        }

        ParseEventCore(node, aggregator);
    }

    public void DiscardIncompleteStreamingState() {
        _toolCalls.Clear();
        _reasoningContentBuilder.Clear();
        _reasoningInProgress = false;
    }

    public void Complete(CompletionAggregator aggregator) {
        ArgumentNullException.ThrowIfNull(aggregator);
        if (!_terminalEventObserved) {
            throw new InvalidDataException(
                "OpenAI chat stream cannot complete without a terminal error or finish_reason."
            );
        }
    }

    private void ParseEventCore(JsonNode? node, CompletionAggregator aggregator) {
        if (node is not JsonObject obj) {
            throw new InvalidDataException("OpenAI chat stream event root must be a JSON object.");
        }

        if (obj.TryGetPropertyValue("error", out var errorNode) && errorNode is not null) {
            if (errorNode is not JsonObject error) {
                throw new InvalidDataException("OpenAI chat stream error must be a JSON object.");
            }

            var errorMessage = error["message"]?.GetValue<string>() ?? "Unknown error";
            DebugUtil.Warning(DebugCategory, $"[OpenAI] API error: {errorMessage}");
            DiscardIncompleteStreamingState();
            aggregator.AbortIncompleteStreamingState();
            aggregator.AppendError(errorMessage);
            aggregator.MarkFailed("error", errorMessage);
            _terminalEventObserved = true;
            return;
        }

        if (obj["choices"] is not JsonArray choices) {
            throw new InvalidDataException("OpenAI chat stream event must contain a choices array.");
        }
        if (choices.Count > 1) {
            throw new InvalidDataException(
                "OpenAI chat client supports only the default n=1 response shape."
            );
        }

        foreach (var choiceNode in choices) {
            if (choiceNode is not JsonObject choice) {
                throw new InvalidDataException("OpenAI chat stream choice must be a JSON object.");
            }
            var choiceIndex = GetRequiredInt(choice, "index", "chat choice");
            if (choiceIndex != 0) {
                throw new InvalidDataException(
                    $"OpenAI chat client supports only choice index 0, but received {choiceIndex}."
                );
            }
            HandleChoice(choice, aggregator);
        }
    }

    private void HandleChoice(JsonObject choice, CompletionAggregator aggregator) {
        if (choice.TryGetPropertyValue("delta", out var deltaNode) && deltaNode is not null) {
            if (deltaNode is not JsonObject delta) {
                throw new InvalidDataException("OpenAI chat stream choice delta must be a JSON object or null.");
            }
            HandleDelta(delta, aggregator);
        }

        var finishReason = choice["finish_reason"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(finishReason)) {
            FlushPendingStreamingState(aggregator);
            RecordTermination(finishReason, aggregator);
            _terminalEventObserved = true;
        }
    }

    private void HandleDelta(JsonObject delta, CompletionAggregator aggregator) {
        var reasoningContent = delta["reasoning_content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(reasoningContent) && _reasoningMode is not OpenAIChatReasoningMode.Ignore) {
            BeginThinkingIfNeeded(aggregator);
            _reasoningContentBuilder.Append(reasoningContent);
            aggregator.AppendReasoningDelta(reasoningContent);
        }

        var hasToolCallsInCurrentDelta = delta["tool_calls"] is JsonArray { Count: > 0 };
        var content = delta["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(content)) {
            FlushPendingReasoning(aggregator);
            if (!ShouldIgnoreContentDelta(content, hasToolCallsInCurrentDelta)) {
                aggregator.AppendContent(content);
            }
        }

        if (delta["tool_calls"] is JsonArray toolCalls) {
            if (_toolCalls.Count == 0) {
                FlushPendingReasoning(aggregator);
            }

            var fallbackIndex = 0;
            foreach (var toolCallNode in toolCalls) {
                if (toolCallNode is JsonObject toolCall) {
                    MergeToolCallDelta(toolCall, fallbackIndex);
                }

                fallbackIndex++;
            }
        }
    }

    private void BeginThinkingIfNeeded(CompletionAggregator aggregator) {
        if (_reasoningInProgress) { return; }

        aggregator.BeginThinking();
        _reasoningInProgress = true;
    }

    private void FlushPendingReasoning(CompletionAggregator aggregator) {
        if (_reasoningContentBuilder.Length == 0) { return; }

        aggregator.EndThinking(
            new OpenAIChatReasoningBlock(
                _reasoningContentBuilder.ToString(),
                aggregator.Invocation
            )
        );

        _reasoningContentBuilder.Clear();
        _reasoningInProgress = false;
    }

    private void FlushPendingStreamingState(CompletionAggregator aggregator) {
        FlushPendingReasoning(aggregator);
        FlushPendingToolCalls(aggregator);
    }

    private bool ShouldIgnoreContentDelta(string content, bool hasToolCallsInCurrentDelta) {
        if (_whitespaceContentMode is not OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls) { return false; }

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

    private RawToolCall CreateToolCall(ToolCallState state) {
        var rawArgumentsText = StreamParserToolUtility.NormalizeRawArgumentsJson(state.ArgumentsBuilder.ToString());

        var toolName = state.ToolName ?? string.Empty;
        var toolCallId = string.IsNullOrWhiteSpace(state.ToolCallId) ? $"openai-call-{state.Index}" : state.ToolCallId;
        return BuildToolCallWithoutSchema(toolName, toolCallId, rawArgumentsText);
    }

    private static RawToolCall BuildToolCallWithoutSchema(string toolName, string toolCallId, string rawArgumentsText)
        => StreamParserToolUtility.BuildToolCallWithoutSchema(toolName, toolCallId, rawArgumentsText);

    private static int GetRequiredInt(
        JsonObject obj,
        string propertyName,
        string context
    ) {
        if (obj[propertyName] is JsonValue value
            && value.TryGetValue<int>(out var result)) {
            return result;
        }

        throw new InvalidDataException(
            $"OpenAI {context} requires integer field '{propertyName}'."
        );
    }

    private static void RecordTermination(string finishReason, CompletionAggregator aggregator) {
        switch (finishReason) {
            case "stop":
            case "tool_calls":
            case "function_call":
                aggregator.MarkCompleted(finishReason);
                break;
            case "length":
            case "content_filter":
                aggregator.MarkIncomplete(finishReason);
                break;
            default:
                aggregator.MarkIncomplete(finishReason, $"Unhandled OpenAI finish_reason '{finishReason}'.");
                break;
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
