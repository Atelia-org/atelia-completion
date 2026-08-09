using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.Anthropic;

/// <summary>
/// 解析 Anthropic SSE 流式响应事件，直接向 <see cref="CompletionAggregator"/> 喂入增量数据。
/// 事件类型：message_start, content_block_start, content_block_delta, content_block_stop, message_delta, message_stop
/// </summary>
internal sealed class AnthropicStreamParser {
    private const string DebugCategory = "Provider";

    private readonly Dictionary<int, ContentBlockState> _contentBlocks = new();
    private string? _stopReason;
    private bool _messageStarted;
    private bool _messageDeltaObserved;
    private int _nextContentBlockIndex;
    private bool _terminalEventObserved;

    public bool TerminalEventObserved => _terminalEventObserved;

    public AnthropicStreamParser() {
    }

    public void ParseEvent(
        string? json,
        CompletionAggregator aggregator,
        string? sseEventType
    ) {
        if (_terminalEventObserved) { return; }

        if (string.IsNullOrWhiteSpace(sseEventType)) {
            throw new InvalidDataException(
                "Anthropic Messages stream requires a named SSE event."
            );
        }
        if (json is null) {
            throw new InvalidDataException(
                $"Anthropic SSE event '{sseEventType}' requires a data field."
            );
        }

        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex) {
            throw new InvalidDataException(
                "Anthropic Messages stream contained malformed provider JSON.",
                ex
            );
        }

        if (node is not JsonObject obj) {
            throw new InvalidDataException(
                "Anthropic Messages stream event root must be a JSON object."
            );
        }

        var eventType = GetRequiredString(obj, "type", "stream event");
        if (!string.Equals(sseEventType, eventType, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"Anthropic SSE event type '{sseEventType}' does not match data type '{eventType}'."
            );
        }

        switch (eventType) {
            case "message_start":
                HandleMessageStart(obj, aggregator);
                break;
            case "content_block_start":
                RequireMessageStarted(eventType);
                HandleContentBlockStart(obj, aggregator);
                break;
            case "content_block_delta":
                RequireMessageStarted(eventType);
                HandleContentBlockDelta(obj, aggregator);
                break;
            case "content_block_stop":
                RequireMessageStarted(eventType);
                HandleContentBlockStop(obj, aggregator);
                break;
            case "message_delta":
                RequireMessageStarted(eventType);
                HandleMessageDelta(obj, aggregator);
                break;
            case "message_stop":
                RequireMessageStarted(eventType);
                HandleMessageStop(aggregator);
                break;
            case "ping":
                break;
            case "error":
                HandleError(obj, aggregator);
                break;
            default:
                HandleUnknownEvent(eventType);
                break;
        }
    }

    public void DiscardIncompleteStreamingState() {
        _contentBlocks.Clear();
    }

    /// <summary>
    /// Applies the authoritative <c>message_delta.stop_reason</c> only after
    /// the transport has reached a clean EOF. This is a narrow compatibility
    /// path for Anthropic-compatible relays that omit the data-free
    /// <c>message_stop</c> event. Read failures, cancellation, malformed
    /// frames, or an active content block never reach this method.
    /// </summary>
    public bool TryFinalizeAtCleanEndOfStream(CompletionAggregator aggregator) {
        if (!_messageStarted
            || !_messageDeltaObserved
            || string.IsNullOrWhiteSpace(_stopReason)
            || _contentBlocks.Count > 0) {
            return false;
        }

        FinalizeFromStopReason(aggregator);
        return true;
    }

    public string DescribeInterruptionState() {
        var activeBlockIndexes = _contentBlocks.Count == 0
            ? "none"
            : string.Join(",", _contentBlocks.Keys.OrderBy(static index => index));
        var stopReason = string.IsNullOrWhiteSpace(_stopReason)
            ? "none"
            : SanitizeDiagnosticToken(_stopReason);

        return $"messageStarted={_messageStarted.ToString().ToLowerInvariant()}, "
            + $"messageDeltaObserved={_messageDeltaObserved.ToString().ToLowerInvariant()}, "
            + $"stopReason={stopReason}, activeBlockIndexes={activeBlockIndexes}";
    }

    private void HandleMessageStart(
        JsonObject obj,
        CompletionAggregator aggregator
    ) {
        if (_messageStarted) {
            throw new InvalidDataException(
                "Anthropic Messages stream contained repeated message_start."
            );
        }

        JsonObject message = GetRequiredObject(
            obj,
            "message",
            "message_start"
        );
        MergeUsageIfPresent(message, aggregator, "message_start message");
        _messageStarted = true;
    }

    private void HandleContentBlockStart(JsonObject obj, CompletionAggregator aggregator) {
        if (_messageDeltaObserved) {
            throw new InvalidDataException(
                "Anthropic content_block_start arrived after message_delta."
            );
        }
        if (_contentBlocks.Count > 0) {
            throw new InvalidDataException(
                "Anthropic content_block_start arrived before the active content block stopped."
            );
        }

        var index = GetRequiredNonNegativeInt(obj, "index", "content_block_start");
        if (index != _nextContentBlockIndex) {
            throw new InvalidDataException(
                $"Anthropic content_block_start expected index {_nextContentBlockIndex}, but received {index}."
            );
        }

        var contentBlock = GetRequiredObject(obj, "content_block", "content_block_start");
        var blockType = GetRequiredString(contentBlock, "type", "content_block_start content_block");

        var state = new ContentBlockState {
            Type = blockType
        };

        if (blockType == "tool_use") {
            state.ToolUseId = GetRequiredString(contentBlock, "id", "tool_use content block");
            state.ToolName = GetRequiredString(contentBlock, "name", "tool_use content block");
        }
        else if (blockType == "thinking") {
            // 通知 aggregator（及 observer）thinking 块开始
            aggregator.BeginThinking();

            // 偶尔 content_block_start 已携带初始 thinking/signature 文本（尽管常见为空），
            // 一并预填，后续 thinking_delta / signature_delta 继续追加。
            var initialThinking = GetOptionalString(contentBlock, "thinking", "thinking content block");
            if (!string.IsNullOrEmpty(initialThinking)) {
                state.ThinkingTextBuilder.Append(initialThinking);
                aggregator.AppendReasoningDelta(initialThinking);
            }
            var initialSignature = GetOptionalString(contentBlock, "signature", "thinking content block");
            if (!string.IsNullOrEmpty(initialSignature)) { state.ThinkingSignatureBuilder.Append(initialSignature); }
        }
        else if (blockType == "redacted_thinking") {
            // 安全系统加密的 thinking：无明文可展示，但必须原样保留以便回灌。
            aggregator.BeginThinking();
            state.RedactedData = GetRequiredString(contentBlock, "data", "redacted_thinking content block");
        }

        _contentBlocks[index] = state;
    }

    private void HandleContentBlockDelta(JsonObject obj, CompletionAggregator aggregator) {
        if (_messageDeltaObserved) {
            throw new InvalidDataException(
                "Anthropic content_block_delta arrived after message_delta."
            );
        }

        var index = GetRequiredNonNegativeInt(obj, "index", "content_block_delta");
        if (!_contentBlocks.TryGetValue(index, out var state)) {
            throw new InvalidDataException(
                $"Anthropic content_block_delta referenced inactive index {index}."
            );
        }

        var delta = GetRequiredObject(obj, "delta", "content_block_delta");
        var deltaType = GetRequiredString(delta, "type", "content_block_delta delta");

        if (deltaType == "text_delta") {
            RequireBlockType(state, "text", deltaType, index);
            var text = GetRequiredString(delta, "text", deltaType, allowEmpty: true);
            if (!string.IsNullOrEmpty(text)) {
                aggregator.AppendContent(text);
            }
        }
        else if (deltaType == "input_json_delta") {
            RequireBlockType(state, "tool_use", deltaType, index);
            var partial = GetRequiredString(delta, "partial_json", deltaType, allowEmpty: true);
            if (!string.IsNullOrEmpty(partial)) {
                state.ToolInputJsonBuilder.Append(partial);
            }
        }
        else if (deltaType == "thinking_delta") {
            RequireBlockType(state, "thinking", deltaType, index);
            var thinkingText = GetRequiredString(delta, "thinking", deltaType, allowEmpty: true);
            if (!string.IsNullOrEmpty(thinkingText)) {
                state.ThinkingTextBuilder.Append(thinkingText);
                aggregator.AppendReasoningDelta(thinkingText);
            }
        }
        else if (deltaType == "signature_delta") {
            RequireBlockType(state, "thinking", deltaType, index);
            var signature = GetRequiredString(delta, "signature", deltaType, allowEmpty: true);
            if (!string.IsNullOrEmpty(signature)) {
                state.ThinkingSignatureBuilder.Append(signature);
            }
        }
    }

    private void HandleContentBlockStop(JsonObject obj, CompletionAggregator aggregator) {
        if (_messageDeltaObserved) {
            throw new InvalidDataException(
                "Anthropic content_block_stop arrived after message_delta."
            );
        }

        var index = GetRequiredNonNegativeInt(obj, "index", "content_block_stop");
        if (!_contentBlocks.TryGetValue(index, out var state)) {
            throw new InvalidDataException(
                $"Anthropic content_block_stop referenced inactive index {index}."
            );
        }

        if (state.Type == "tool_use") {
            var toolCall = CreateToolCallRequest(state);
            aggregator.AppendToolCall(toolCall);
        }
        else if (state.Type == "thinking") {
            var thinkingText = state.ThinkingTextBuilder.ToString();
            var signature = state.ThinkingSignatureBuilder.ToString();
            if (string.IsNullOrWhiteSpace(signature)) {
                throw new InvalidDataException(
                    "Anthropic thinking content block stopped without a non-empty signature."
                );
            }
            var payloadBytes = AnthropicThinkingPayloadCodec.Encode(thinkingText, signature);

            aggregator.EndThinking(
                new AnthropicReasoningBlock(
                    OpaquePayload: payloadBytes,
                    Origin: aggregator.Invocation,
                    PlainText: string.IsNullOrEmpty(thinkingText) ? null : thinkingText
                )
            );
        }
        else if (state.Type == "redacted_thinking") {
            aggregator.EndThinking(
                new AnthropicReasoningBlock(
                    OpaquePayload: AnthropicThinkingPayloadCodec.EncodeRedacted(state.RedactedData),
                    Origin: aggregator.Invocation,
                    PlainText: null
                )
            );
        }

        _contentBlocks.Remove(index);
        _nextContentBlockIndex++;
    }

    private void HandleMessageDelta(
        JsonObject obj,
        CompletionAggregator aggregator
    ) {
        if (_contentBlocks.Count > 0) {
            throw new InvalidDataException(
                "Anthropic message_delta arrived before the active content block stopped."
            );
        }

        var delta = GetRequiredObject(obj, "delta", "message_delta");
        MergeUsageIfPresent(obj, aggregator, "message_delta");

        var stopReason = GetOptionalString(delta, "stop_reason", "message_delta delta");
        if (!string.IsNullOrWhiteSpace(stopReason)) {
            if (_stopReason is not null
                && !string.Equals(_stopReason, stopReason, StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    $"Anthropic message_delta changed stop_reason from '{_stopReason}' to '{stopReason}'."
                );
            }
            _stopReason = stopReason;
        }
        _messageDeltaObserved = true;
    }

    private static void MergeUsageIfPresent(
        JsonObject envelope,
        CompletionAggregator aggregator,
        string context
    ) {
        if (!envelope.TryGetPropertyValue("usage", out JsonNode? usageNode)
            || usageNode is null) {
            return;
        }
        if (usageNode is not JsonObject usage) {
            throw new InvalidDataException(
                $"Anthropic {context} field 'usage' must be an object or null."
            );
        }

        long? input = GetOptionalNonNegativeLong(
            usage,
            "input_tokens",
            $"{context} usage"
        );
        long? output = GetOptionalNonNegativeLong(
            usage,
            "output_tokens",
            $"{context} usage"
        );
        long? creation = GetOptionalNonNegativeLong(
            usage,
            "cache_creation_input_tokens",
            $"{context} usage"
        );
        long? read = GetOptionalNonNegativeLong(
            usage,
            "cache_read_input_tokens",
            $"{context} usage"
        );
        bool hasCreation = creation is not null;
        bool hasRead = read is not null;

        aggregator.MergeUsage(
            new CompletionUsage(
                input,
                creation,
                read,
                output,
                new PromptCacheTelemetry(
                    observationStatus: hasCreation && hasRead
                        ? PromptCacheObservationStatus.Complete
                        : hasCreation || hasRead
                            ? PromptCacheObservationStatus.Partial
                            : PromptCacheObservationStatus.Unavailable
                )
            )
        );
    }

    private void HandleMessageStop(CompletionAggregator aggregator) {
        if (_contentBlocks.Count > 0) {
            throw new InvalidDataException(
                "Anthropic message_stop arrived with an active content block."
            );
        }
        if (!_messageDeltaObserved || string.IsNullOrWhiteSpace(_stopReason)) {
            throw new InvalidDataException(
                "Anthropic message_stop requires a preceding message_delta with stop_reason."
            );
        }

        FinalizeFromStopReason(aggregator);
        _terminalEventObserved = true;
    }

    private void FinalizeFromStopReason(CompletionAggregator aggregator) {
        aggregator.AbortIncompleteStreamingState();
        switch (_stopReason) {
            case "end_turn":
            case "tool_use":
                aggregator.MarkCompleted(_stopReason);
                break;
            default:
                aggregator.MarkIncomplete(_stopReason);
                break;
        }
    }

    private static string SanitizeDiagnosticToken(string value) {
        const int MaximumLength = 80;
        var sanitized = new string(
            value
                .Take(MaximumLength)
                .Select(static character => char.IsControl(character) ? '?' : character)
                .ToArray()
        );
        return value.Length > MaximumLength ? $"{sanitized}..." : sanitized;
    }

    private void HandleError(JsonObject obj, CompletionAggregator aggregator) {
        var errorObject = GetRequiredObject(obj, "error", "error event");
        var errorType = GetRequiredString(errorObject, "type", "error event error");
        var errorMessage = GetRequiredString(errorObject, "message", "error event error");

        FinalizeTerminalStreamingState(aggregator);
        DebugUtil.Warning(DebugCategory, $"[Anthropic] API error type={errorType}: {errorMessage}");
        aggregator.AppendError(errorMessage);
        aggregator.MarkFailed(errorType, errorMessage);
        _terminalEventObserved = true;
    }

    private void HandleUnknownEvent(string eventType) {
        DebugUtil.Warning(DebugCategory, $"[Anthropic] Unknown event type: {eventType}");
    }

    private void RequireMessageStarted(string eventType) {
        if (!_messageStarted) {
            throw new InvalidDataException(
                $"Anthropic {eventType} arrived before message_start."
            );
        }
    }

    private void FinalizeTerminalStreamingState(CompletionAggregator aggregator) {
        if (_contentBlocks.Count > 0) {
            var pendingIndexes = string.Join(", ", _contentBlocks.Keys.OrderBy(static index => index));
            DebugUtil.Warning(
                DebugCategory,
                $"[Anthropic] Terminal event arrived with unfinished content blocks indexes=[{pendingIndexes}]."
            );
            aggregator.MarkIncomplete(
                detail: $"Anthropic terminal event arrived with unfinished content blocks [{pendingIndexes}]."
            );
        }

        DiscardIncompleteStreamingState();
        aggregator.AbortIncompleteStreamingState();
    }

    /// <summary>
    /// Builds a <see cref="RawToolCall"/> from a completed Anthropic tool content block.
    /// </summary>
    /// <remarks>
    /// Preserves the provider-emitted JSON text for downstream replay and execution-boundary parsing.
    /// </remarks>
    private RawToolCall CreateToolCallRequest(ContentBlockState state) {
        var rawArgumentsText = StreamParserToolUtility.NormalizeRawArgumentsJson(state.ToolInputJsonBuilder.ToString());
        return BuildToolCallWithoutSchema(state.ToolName, state.ToolUseId, rawArgumentsText);
    }

    private static RawToolCall BuildToolCallWithoutSchema(string toolName, string toolCallId, string rawArgumentsText)
        => StreamParserToolUtility.BuildToolCallWithoutSchema(toolName, toolCallId, rawArgumentsText);

    private static JsonObject GetRequiredObject(
        JsonObject obj,
        string propertyName,
        string context
    ) {
        if (obj[propertyName] is JsonObject value) { return value; }

        throw new InvalidDataException(
            $"Anthropic {context} requires object field '{propertyName}'."
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
            && (allowEmpty || !string.IsNullOrWhiteSpace(result))) { return result; }

        throw new InvalidDataException(
            $"Anthropic {context} requires string field '{propertyName}'."
        );
    }

    private static string? GetOptionalString(
        JsonObject obj,
        string propertyName,
        string context
    ) {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null) { return null; }
        if (node is JsonValue value && value.TryGetValue<string>(out var result)) { return result; }

        throw new InvalidDataException(
            $"Anthropic {context} field '{propertyName}' must be a string or null."
        );
    }

    private static int GetRequiredNonNegativeInt(
        JsonObject obj,
        string propertyName,
        string context
    ) {
        if (obj[propertyName] is JsonValue value
            && value.TryGetValue<int>(out var result)
            && result >= 0) { return result; }

        throw new InvalidDataException(
            $"Anthropic {context} requires non-negative integer field '{propertyName}'."
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
            $"Anthropic {context} field '{propertyName}' must be a non-negative integer or null."
        );
    }

    private static void RequireBlockType(
        ContentBlockState state,
        string expectedType,
        string deltaType,
        int index
    ) {
        if (string.Equals(state.Type, expectedType, StringComparison.Ordinal)) { return; }

        throw new InvalidDataException(
            $"Anthropic {deltaType} at index {index} requires active "
            + $"content block type '{expectedType}', but found '{state.Type}'."
        );
    }

    private sealed class ContentBlockState {
        public string Type { get; set; } = string.Empty;
        public string ToolUseId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public StringBuilder ToolInputJsonBuilder { get; } = new();
        public StringBuilder ThinkingTextBuilder { get; } = new();
        public StringBuilder ThinkingSignatureBuilder { get; } = new();
        public string RedactedData { get; set; } = string.Empty;
    }
}
