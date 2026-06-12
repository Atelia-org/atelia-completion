using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.Gemini;

internal sealed class GeminiStreamParser {
    private const string DebugCategory = "Provider";

    private readonly List<GeminiReplayPayloadCodec.GeminiReplayPayloadPart> _replayParts = new();
    private bool _sawFinishReason;

    public void ParseEvent(string json, CompletionAggregator aggregator) {
        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex) {
            DebugUtil.Warning(DebugCategory, $"[Gemini] Failed to parse event: {ex.Message}", ex);
            return;
        }

        if (node is not JsonObject obj) { return; }

        if (obj["error"] is JsonObject error) {
            var errorMessage = error["message"]?.GetValue<string>() ?? "Unknown error";
            DebugUtil.Warning(DebugCategory, $"[Gemini] API error: {errorMessage}");
            aggregator.AppendError(errorMessage);
            aggregator.MarkFailed("error", errorMessage);
            return;
        }

        if (obj["candidates"] is not JsonArray candidates) { return; }

        foreach (var candidateNode in candidates) {
            if (candidateNode is JsonObject candidate) {
                HandleCandidate(candidate, aggregator);
            }
        }
    }

    public void Complete(CompletionAggregator aggregator) {
        if (!_sawFinishReason) {
            aggregator.MarkIncomplete(detail: "Gemini stream ended without finishReason.");
        }

        EmitReplayBlockIfNeeded(aggregator);
    }

    public void DiscardIncompleteStreamingState() {
        _replayParts.Clear();
    }

    private void HandleCandidate(JsonObject candidate, CompletionAggregator aggregator) {
        if (candidate["content"] is JsonObject content && content["parts"] is JsonArray parts) {
            foreach (var partNode in parts) {
                if (partNode is JsonObject part) {
                    HandlePart(part, aggregator);
                }
            }
        }

        var finishReason = candidate["finishReason"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(finishReason)) {
            _sawFinishReason = true;
            EmitReplayBlockIfNeeded(aggregator);
            RecordTermination(finishReason, aggregator);
        }
    }

    private void HandlePart(JsonObject part, CompletionAggregator aggregator) {
        var thoughtSignature = part["thoughtSignature"]?.GetValue<string>();

        if (part["functionCall"] is JsonObject functionCall) {
            var toolName = functionCall["name"]?.GetValue<string>() ?? string.Empty;
            var toolCallId = functionCall["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(toolCallId)) {
                toolCallId = $"gemini-call-{_replayParts.Count}";
            }

            var rawArgumentsJson = functionCall["args"]?.ToJsonString() ?? "{}";
            aggregator.AppendToolCall(
                StreamParserToolUtility.BuildToolCallWithoutSchema(toolName, toolCallId, rawArgumentsJson)
            );

            _replayParts.Add(
                new GeminiReplayPayloadCodec.GeminiReplayPayloadPart(
                    Text: null,
                    ThoughtSignature: string.IsNullOrWhiteSpace(thoughtSignature) ? null : thoughtSignature,
                    FunctionCall: new GeminiReplayPayloadCodec.GeminiReplayPayloadFunctionCall(
                        toolName,
                        toolCallId,
                        rawArgumentsJson
                    )
                )
            );

            return;
        }

        if (part["text"] is JsonNode textNode) {
            var text = textNode.GetValue<string>();
            if (!string.IsNullOrEmpty(text)) {
                aggregator.AppendContent(text);
            }

            _replayParts.Add(
                new GeminiReplayPayloadCodec.GeminiReplayPayloadPart(
                    Text: text,
                    ThoughtSignature: string.IsNullOrWhiteSpace(thoughtSignature) ? null : thoughtSignature,
                    FunctionCall: null
                )
            );

            return;
        }

        if (!string.IsNullOrWhiteSpace(thoughtSignature)) {
            _replayParts.Add(
                new GeminiReplayPayloadCodec.GeminiReplayPayloadPart(
                    Text: string.Empty,
                    ThoughtSignature: thoughtSignature,
                    FunctionCall: null
                )
            );
        }
    }

    private void EmitReplayBlockIfNeeded(CompletionAggregator aggregator) {
        if (_replayParts.Count == 0) { return; }

        var payload = GeminiReplayPayloadCodec.Encode("model", _replayParts);
        var plainText = string.Concat(
            _replayParts
                .Where(static part => !string.IsNullOrEmpty(part.Text))
                .Select(static part => part.Text)
        );

        aggregator.AppendReplayBlock(
            new GeminiReplayBlock(
                payload,
                aggregator.Invocation,
                string.IsNullOrEmpty(plainText) ? null : plainText
            )
        );

        _replayParts.Clear();
    }

    private static void RecordTermination(string finishReason, CompletionAggregator aggregator) {
        switch (finishReason) {
            case "STOP":
                aggregator.MarkCompleted(finishReason);
                break;
            default:
                aggregator.MarkIncomplete(finishReason);
                break;
        }
    }
}
