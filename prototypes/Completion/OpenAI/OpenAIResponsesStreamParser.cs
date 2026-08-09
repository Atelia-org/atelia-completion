using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

/// <summary>
/// 解析 OpenAI Responses SSE 事件流，直接向 <see cref="CompletionAggregator"/> 喂入增量数据。
/// </summary>
internal sealed class OpenAIResponsesStreamParser {
    private const string DebugCategory = "Provider";
    private const string FunctionCallItemType = "function_call";

    private readonly Dictionary<string, FunctionCallState> _functionCalls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedFunctionCallItemIds = new(StringComparer.Ordinal);
    private string? _activeReasoningItemId;
    private StringBuilder? _activeReasoningSummary;
    private bool _terminalEventObserved;

    public bool TerminalEventObserved => _terminalEventObserved;

    public void ParseEvent(
        string json,
        CompletionAggregator aggregator,
        string? sseEventType = null
    ) {
        if (_terminalEventObserved) { return; }

        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex) {
            throw new InvalidDataException("OpenAI Responses stream contained malformed provider JSON.", ex);
        }

        ParseEventCore(node, aggregator, sseEventType);
    }

    public void DiscardIncompleteStreamingState() {
        _functionCalls.Clear();
        _completedFunctionCallItemIds.Clear();
        _activeReasoningItemId = null;
        _activeReasoningSummary = null;
    }

    private void ParseEventCore(
        JsonNode? node,
        CompletionAggregator aggregator,
        string? sseEventType
    ) {
        if (node is not JsonObject obj) {
            throw new InvalidDataException("OpenAI Responses stream event root must be a JSON object.");
        }

        var eventType = GetRequiredString(obj, "type", "stream event");

        if (!string.IsNullOrWhiteSpace(sseEventType)
            && !string.Equals(sseEventType, "message", StringComparison.Ordinal)
            && !string.Equals(sseEventType, eventType, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"OpenAI Responses SSE event type '{sseEventType}' does not match data type '{eventType}'."
            );
        }

        if (obj.TryGetPropertyValue("error", out var errorNode)
            && errorNode is not null) {
            if (!string.Equals(eventType, "error", StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    $"OpenAI Responses event type '{eventType}' cannot contain a top-level error object."
                );
            }
            if (errorNode is not JsonObject) {
                throw new InvalidDataException(
                    "OpenAI Responses stream error field must be a JSON object."
                );
            }
        }

        switch (eventType) {
            case "response.output_text.delta":
                var delta = GetRequiredString(obj, "delta", eventType, allowEmpty: true);
                if (delta.Length > 0) {
                    aggregator.AppendContent(delta);
                }
                break;

            case "response.output_item.added":
                HandleOutputItemAdded(obj, aggregator);
                break;

            case "response.function_call_arguments.delta":
                HandleFunctionCallArgumentsDelta(obj);
                break;

            case "response.function_call_arguments.done":
                HandleFunctionCallArgumentsDone(obj, aggregator);
                break;

            case "response.output_item.done":
                HandleOutputItemDone(obj, aggregator);
                break;

            case "response.reasoning_summary_text.delta":
                HandleReasoningSummaryDelta(obj, aggregator);
                break;

            case "response.completed":
                MergeTerminalUsageIfPresent(obj, aggregator);
                aggregator.MarkCompleted("response.completed");
                FinalizeTerminalStreamingState(aggregator);
                _terminalEventObserved = true;
                break;

            case "response.incomplete":
                MergeTerminalUsageIfPresent(obj, aggregator);
                var incompleteReason = ExtractIncompleteReason(obj);
                FinalizeTerminalStreamingState(aggregator);
                aggregator.MarkIncomplete(
                    incompleteReason ?? "response.incomplete",
                    incompleteReason is null
                        ? "OpenAI Responses returned response.incomplete."
                        : $"OpenAI Responses returned response.incomplete: {incompleteReason}."
                );
                _terminalEventObserved = true;
                break;

            case "response.failed":
                MergeTerminalUsageIfPresent(obj, aggregator);
                var failedMessage = ExtractErrorMessage(
                    obj,
                    "OpenAI Responses stream failed."
                );
                FinalizeTerminalStreamingState(aggregator);
                aggregator.AppendError(failedMessage);
                aggregator.MarkFailed(eventType, failedMessage);
                _terminalEventObserved = true;
                break;

            case "error":
                var errorMessage = ExtractErrorMessage(obj, "OpenAI Responses stream failed.");
                FinalizeTerminalStreamingState(aggregator);
                aggregator.AppendError(errorMessage);
                aggregator.MarkFailed(eventType, errorMessage);
                _terminalEventObserved = true;
                break;
        }
    }

    private static void MergeTerminalUsageIfPresent(
        JsonObject envelope,
        CompletionAggregator aggregator
    ) {
        if (envelope["response"] is not JsonObject response
            || !response.TryGetPropertyValue("usage", out JsonNode? usageNode)
            || usageNode is null) {
            return;
        }
        if (usageNode is not JsonObject usage) {
            throw new InvalidDataException(
                "OpenAI Responses terminal response field 'usage' must be an object or null."
            );
        }

        long? inputTokens = GetOptionalNonNegativeLong(
            usage,
            "input_tokens",
            "Responses usage"
        );
        long? outputTokens = GetOptionalNonNegativeLong(
            usage,
            "output_tokens",
            "Responses usage"
        );
        long? cachedTokens = null;
        long? cacheWriteTokens = null;
        bool readObserved = false;
        bool writeObserved = false;
        if (usage.TryGetPropertyValue(
                "input_tokens_details",
                out JsonNode? detailsNode
            )
            && detailsNode is not null) {
            if (detailsNode is not JsonObject details) {
                throw new InvalidDataException(
                    "OpenAI Responses usage field 'input_tokens_details' must be an object or null."
                );
            }
            cachedTokens = GetOptionalNonNegativeLong(
                details,
                "cached_tokens",
                "Responses input_tokens_details"
            );
            cacheWriteTokens = GetOptionalNonNegativeLong(
                details,
                "cache_write_tokens",
                "Responses input_tokens_details"
            );
            readObserved = cachedTokens is not null;
            writeObserved = cacheWriteTokens is not null;
        }

        aggregator.MergeUsage(
            new CompletionUsage(
                SubtractCacheIoTokens(
                    inputTokens,
                    cachedTokens,
                    cacheWriteTokens
                ),
                cacheWriteTokens,
                cachedTokens,
                outputTokens,
                new PromptCacheTelemetry(
                    observationStatus: readObserved && writeObserved
                        ? PromptCacheObservationStatus.Complete
                        : readObserved || writeObserved
                            ? PromptCacheObservationStatus.Partial
                            : PromptCacheObservationStatus.Unavailable
                )
            )
        );
    }

    private void FinalizeTerminalStreamingState(CompletionAggregator aggregator) {
        if (_activeReasoningItemId is not null) {
            DebugUtil.Warning(
                DebugCategory,
                $"[OpenAI/Responses] Terminal event arrived with unfinished reasoning item_id={_activeReasoningItemId}."
            );
            aggregator.MarkIncomplete(detail: "OpenAI Responses terminal event arrived with unfinished reasoning.");
        }

        if (_functionCalls.Count > 0) {
            var pendingIds = string.Join(", ", _functionCalls.Keys.OrderBy(static id => id));
            DebugUtil.Warning(
                DebugCategory,
                $"[OpenAI/Responses] Terminal event arrived with unfinished function calls item_ids=[{pendingIds}]."
            );
            aggregator.MarkIncomplete(detail: $"OpenAI Responses terminal event arrived with unfinished function calls [{pendingIds}].");
        }

        DiscardIncompleteStreamingState();
        aggregator.AbortIncompleteStreamingState();
    }

    private void HandleOutputItemAdded(JsonObject obj, CompletionAggregator aggregator) {
        var item = GetRequiredObject(obj, "item", "response.output_item.added");

        var itemType = GetRequiredString(item, "type", "response.output_item.added item");
        switch (itemType) {
            case FunctionCallItemType:
                GetOrCreateFunctionCallState(obj, item);
                break;

            case "reasoning":
                BeginReasoningIfNeeded(obj, item, aggregator);
                break;
        }
    }

    private void HandleFunctionCallArgumentsDelta(JsonObject obj) {
        var state = GetOrCreateFunctionCallState(obj, obj["item"] as JsonObject);
        if (state is null) { return; }

        var delta = GetRequiredString(
            obj,
            "delta",
            "response.function_call_arguments.delta",
            allowEmpty: true
        );
        state.ArgumentsBuilder.Append(delta);
    }

    private void HandleFunctionCallArgumentsDone(JsonObject obj, CompletionAggregator aggregator) {
        var state = GetOrCreateFunctionCallState(obj, obj["item"] as JsonObject);
        if (state is null) { return; }

        var arguments = GetRequiredString(
            obj,
            "arguments",
            "response.function_call_arguments.done",
            allowEmpty: true
        );
        state.SetArguments(arguments);

        if (obj["item"] is JsonObject item) {
            UpdateFunctionCallMetadata(state, obj, item);
        }

        FinalizeFunctionCall(state, aggregator);
    }

    private void HandleOutputItemDone(JsonObject obj, CompletionAggregator aggregator) {
        var item = GetRequiredObject(obj, "item", "response.output_item.done");

        var itemType = GetRequiredString(item, "type", "response.output_item.done item");
        switch (itemType) {
            case FunctionCallItemType:
                var itemId = GetItemId(obj, item);
                if (!string.IsNullOrWhiteSpace(itemId) && _completedFunctionCallItemIds.Contains(itemId)) { return; }

                var state = GetOrCreateFunctionCallState(obj, item);
                if (state is not null) {
                    FinalizeFunctionCall(state, aggregator);
                }
                break;

            case "reasoning":
                FinalizeReasoningItem(item, aggregator);
                break;
        }
    }

    private void HandleReasoningSummaryDelta(
        JsonObject obj,
        CompletionAggregator aggregator
    ) {
        var itemId = GetRequiredString(
            obj,
            "item_id",
            "response.reasoning_summary_text.delta"
        );
        if (!string.Equals(_activeReasoningItemId, itemId, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "OpenAI reasoning summary delta does not match the active reasoning item."
            );
        }
        var delta = GetRequiredString(
            obj,
            "delta",
            "response.reasoning_summary_text.delta",
            allowEmpty: true
        );
        if (delta.Length > 0) {
            _activeReasoningSummary ??= new StringBuilder();
            _activeReasoningSummary.Append(delta);
            aggregator.AppendReasoningDelta(delta);
        }
    }

    private void BeginReasoningIfNeeded(JsonObject obj, JsonObject item, CompletionAggregator aggregator) {
        var itemId = GetItemId(obj, item);
        if (string.IsNullOrWhiteSpace(itemId)) {
            throw new InvalidDataException(
                "OpenAI response.output_item.added reasoning item requires an id."
            );
        }
        if (_activeReasoningItemId == itemId) { return; }

        if (_activeReasoningItemId is not null) {
            DebugUtil.Warning(
                DebugCategory,
                $"[OpenAI/Responses] Reasoning item switched from {_activeReasoningItemId} to {itemId} before completion."
            );
        }

        aggregator.BeginThinking();
        _activeReasoningItemId = itemId;
        _activeReasoningSummary = new StringBuilder();
    }

    private void FinalizeReasoningItem(JsonObject item, CompletionAggregator aggregator) {
        var itemId = GetRequiredString(item, "id", "reasoning output item");
        string rawItemJson = item.ToJsonString();
        string? plainText = OpenAIResponsesReasoningBlock.ExtractPlainText(rawItemJson);
        var block = new OpenAIResponsesReasoningBlock(
            rawItemJson,
            aggregator.Invocation,
            plainText
        );

        if (!string.IsNullOrWhiteSpace(itemId) && string.Equals(_activeReasoningItemId, itemId, StringComparison.Ordinal)) {
            string? streamedPlainText = _activeReasoningSummary is { Length: > 0 }
                ? _activeReasoningSummary.ToString()
                : null;
            if (streamedPlainText is not null
                && !string.Equals(streamedPlainText, plainText, StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "OpenAI reasoning summary deltas do not match the completed reasoning item."
                );
            }
            aggregator.EndThinking(block);
            _activeReasoningItemId = null;
            _activeReasoningSummary = null;
            return;
        }

        if (_activeReasoningItemId is null) {
            aggregator.AppendThinking(block);
            return;
        }

        DebugUtil.Warning(
            DebugCategory,
            $"[OpenAI/Responses] Reasoning item done mismatch active={_activeReasoningItemId}, item={itemId ?? "<null>"}."
        );
        aggregator.EndThinking(block);
        _activeReasoningItemId = null;
        _activeReasoningSummary = null;
    }

    private FunctionCallState? GetOrCreateFunctionCallState(JsonObject envelope, JsonObject? item) {
        var itemId = GetItemId(envelope, item);
        if (string.IsNullOrWhiteSpace(itemId)) {
            throw new InvalidDataException(
                "OpenAI Responses function_call event requires item_id or item.id."
            );
        }

        if (_completedFunctionCallItemIds.Contains(itemId)) { return null; }

        if (!_functionCalls.TryGetValue(itemId, out var state)) {
            state = new FunctionCallState(itemId);
            _functionCalls[itemId] = state;
        }

        UpdateFunctionCallMetadata(state, envelope, item);
        return state;
    }

    private static string? GetItemId(JsonObject envelope, JsonObject? item) {
        return item?["id"]?.GetValue<string>()
            ?? envelope["item_id"]?.GetValue<string>()
            ?? envelope["output_item_id"]?.GetValue<string>();
    }

    private static void UpdateFunctionCallMetadata(FunctionCallState state, JsonObject envelope, JsonObject? item) {
        var outputIndex = envelope["output_index"]?.GetValue<int>();
        if (outputIndex.HasValue) {
            state.OutputIndex = outputIndex.Value;
        }

        var callId = item?["call_id"]?.GetValue<string>() ?? envelope["call_id"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(callId)) {
            state.CallId = callId;
        }

        var toolName = item?["name"]?.GetValue<string>() ?? envelope["name"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(toolName)) {
            state.ToolName = toolName;
        }

        var arguments = item?["arguments"]?.GetValue<string>() ?? envelope["arguments"]?.GetValue<string>();
        if (arguments is not null) {
            state.SetArguments(arguments);
        }
    }

    private void FinalizeFunctionCall(FunctionCallState state, CompletionAggregator aggregator) {
        _functionCalls.Remove(state.ItemId);
        _completedFunctionCallItemIds.Add(state.ItemId);

        var rawArgumentsText = StreamParserToolUtility.NormalizeRawArgumentsJson(state.ArgumentsBuilder.ToString());
        var toolName = state.ToolName ?? string.Empty;
        var toolCallId = string.IsNullOrWhiteSpace(state.CallId)
            ? $"openai-responses-call-{state.OutputIndex?.ToString() ?? state.ItemId}"
            : state.CallId;

        aggregator.AppendToolCall(
            StreamParserToolUtility.BuildToolCallWithoutSchema(toolName, toolCallId, rawArgumentsText)
        );
    }

    private static string ExtractErrorMessage(JsonObject obj, string fallbackMessage) {
        var directMessage = obj["message"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(directMessage)) { return directMessage; }

        if (obj["error"] is JsonObject nestedError) { return ExtractErrorMessage(nestedError, fallbackMessage); }

        if (obj["response"] is JsonObject response) { return ExtractErrorMessage(response, fallbackMessage); }

        return fallbackMessage;
    }

    private static string? ExtractIncompleteReason(JsonObject obj) {
        if (obj["response"] is not JsonObject response) { return null; }
        if (response["incomplete_details"] is not JsonObject details) { return null; }
        return details["reason"]?.GetValue<string>();
    }

    private static JsonObject GetRequiredObject(
        JsonObject obj,
        string propertyName,
        string context
    ) {
        if (obj[propertyName] is JsonObject value) { return value; }

        throw new InvalidDataException(
            $"OpenAI {context} requires object field '{propertyName}'."
        );
    }

    private static string GetRequiredString(
        JsonObject obj,
        string propertyName,
        string context,
        bool allowEmpty = false
    ) {
        if (obj[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && (allowEmpty || !string.IsNullOrWhiteSpace(result))) {
            return result;
        }

        throw new InvalidDataException(
            $"OpenAI {context} requires string field '{propertyName}'."
        );
    }

    private static long? GetOptionalNonNegativeLong(
        JsonObject obj,
        string propertyName,
        string context
    ) {
        if (!obj.TryGetPropertyValue(propertyName, out JsonNode? node)
            || node is null) {
            return null;
        }
        if (node is JsonValue value
            && value.TryGetValue<long>(out long result)
            && result >= 0) {
            return result;
        }
        throw new InvalidDataException(
            $"OpenAI {context} field '{propertyName}' must be a non-negative integer or null."
        );
    }

    private static long? SubtractCacheIoTokens(
        long? total,
        long? read,
        long? write
    ) {
        if (total is null || read is null || write is null) { return null; }
        if (read.Value > total.Value - write.Value) {
            throw new InvalidDataException(
                "OpenAI Responses usage reported cache read plus write tokens greater than total input tokens."
            );
        }
        return total.Value - read.Value - write.Value;
    }

    private sealed class FunctionCallState {
        public FunctionCallState(string itemId) {
            ItemId = itemId;
        }

        public string ItemId { get; }
        public int? OutputIndex { get; set; }
        public string? CallId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder ArgumentsBuilder { get; } = new();

        public void SetArguments(string arguments) {
            ArgumentsBuilder.Clear();
            ArgumentsBuilder.Append(arguments);
        }
    }
}
