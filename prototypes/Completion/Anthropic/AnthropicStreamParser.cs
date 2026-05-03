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

    public AnthropicStreamParser() {
    }

    public void ParseEvent(string json, CompletionAggregator aggregator) {
        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex) {
            DebugUtil.Warning(DebugCategory, $"[Anthropic] Failed to parse event: {ex.Message}", ex);
            return;
        }

        if (node is not JsonObject obj) { return; }

        var eventType = obj["type"]?.GetValue<string>();
        if (eventType is null) { return; }

        switch (eventType) {
            case "message_start":
                break;
            case "content_block_start":
                HandleContentBlockStart(obj, aggregator);
                break;
            case "content_block_delta":
                HandleContentBlockDelta(obj, aggregator);
                break;
            case "content_block_stop":
                HandleContentBlockStop(obj, aggregator);
                break;
            case "message_delta":
                break;
            case "message_stop":
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

    private void HandleContentBlockStart(JsonObject obj, CompletionAggregator aggregator) {
        var index = obj["index"]?.GetValue<int>() ?? -1;
        if (index < 0) { return; }

        var contentBlock = obj["content_block"] as JsonObject;
        if (contentBlock is null) { return; }

        var blockType = contentBlock["type"]?.GetValue<string>();

        var state = new ContentBlockState {
            Type = blockType ?? "unknown"
        };

        if (blockType == "tool_use") {
            state.ToolUseId = contentBlock["id"]?.GetValue<string>() ?? string.Empty;
            state.ToolName = contentBlock["name"]?.GetValue<string>() ?? string.Empty;
        }
        else if (blockType == "thinking") {
            // 通知 aggregator（及 observer）thinking 块开始
            aggregator.BeginThinking();

            // 偶尔 content_block_start 已携带初始 thinking/signature 文本（尽管常见为空），
            // 一并预填，后续 thinking_delta / signature_delta 继续追加。
            var initialThinking = contentBlock["thinking"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(initialThinking)) {
                state.ThinkingTextBuilder.Append(initialThinking);
                aggregator.AppendReasoningDelta(initialThinking);
            }
            var initialSignature = contentBlock["signature"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(initialSignature)) { state.ThinkingSignatureBuilder.Append(initialSignature); }
        }

        _contentBlocks[index] = state;
    }

    private void HandleContentBlockDelta(JsonObject obj, CompletionAggregator aggregator) {
        var index = obj["index"]?.GetValue<int>() ?? -1;
        if (index < 0 || !_contentBlocks.TryGetValue(index, out var state)) { return; }

        var delta = obj["delta"] as JsonObject;
        if (delta is null) { return; }

        var deltaType = delta["type"]?.GetValue<string>();

        if (deltaType == "text_delta") {
            var text = delta["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text)) {
                aggregator.AppendContent(text);
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
                aggregator.AppendReasoningDelta(thinkingText);
            }
        }
        else if (deltaType == "signature_delta") {
            var signature = delta["signature"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(signature)) {
                state.ThinkingSignatureBuilder.Append(signature);
            }
        }
    }

    private void HandleContentBlockStop(JsonObject obj, CompletionAggregator aggregator) {
        var index = obj["index"]?.GetValue<int>() ?? -1;
        if (index < 0 || !_contentBlocks.TryGetValue(index, out var state)) { return; }

        if (state.Type == "tool_use") {
            var toolCall = CreateToolCallRequest(state);
            aggregator.AppendToolCall(toolCall);
        }
        else if (state.Type == "thinking") {
            var thinkingText = state.ThinkingTextBuilder.ToString();
            var signature = state.ThinkingSignatureBuilder.ToString();
            var payloadBytes = AnthropicThinkingPayloadCodec.Encode(thinkingText, string.IsNullOrEmpty(signature) ? null : signature);

            aggregator.EndThinking(
                new AnthropicReasoningBlock(
                    OpaquePayload: payloadBytes,
                    Origin: aggregator.Invocation,
                    PlainTextForDebug: string.IsNullOrEmpty(thinkingText) ? null : thinkingText
                )
            );
        }

        _contentBlocks.Remove(index);
    }

    private void HandleError(JsonObject obj, CompletionAggregator aggregator) {
        var error = obj["error"]?["message"]?.GetValue<string>() ?? "Unknown error";
        DebugUtil.Warning(DebugCategory, $"[Anthropic] API error: {error}");
        aggregator.AppendError(error);
    }

    private void HandleUnknownEvent(string eventType) {
        DebugUtil.Warning(DebugCategory, $"[Anthropic] Unknown event type: {eventType}");
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

    private static bool TryGetInt32(JsonObject obj, string propertyName, out int value) {
        value = default;

        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null) { return false; }

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

    private sealed class ContentBlockState {
        public string Type { get; set; } = string.Empty;
        public string ToolUseId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public StringBuilder ToolInputJsonBuilder { get; } = new();
        public StringBuilder ThinkingTextBuilder { get; } = new();
        public StringBuilder ThinkingSignatureBuilder { get; } = new();
    }
}
