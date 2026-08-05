using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.Gemini;

internal sealed class GeminiStreamParser {
    private const string DebugCategory = "Provider";

    private readonly List<GeminiReplayPayloadCodec.GeminiReplayPayloadPart> _replayParts = new();
    private bool _terminalEventObserved;

    public bool TerminalEventObserved => _terminalEventObserved;

    public void ParseEvent(string json, CompletionAggregator aggregator) {
        if (_terminalEventObserved) { return; }

        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex) {
            throw new InvalidDataException(
                "Gemini streamGenerateContent stream contained malformed provider JSON.",
                ex
            );
        }

        if (node is not JsonObject obj) {
            throw new InvalidDataException(
                "Gemini streamGenerateContent response root must be a JSON object."
            );
        }

        if (obj.TryGetPropertyValue("error", out var errorNode)) {
            HandleError(errorNode, aggregator);
            return;
        }

        var promptBlock = GetPromptBlock(obj);
        if (!obj.TryGetPropertyValue("candidates", out var candidatesNode)
            || candidatesNode is null) {
            if (promptBlock is not null) {
                HandlePromptBlock(promptBlock, aggregator);
            }
            return;
        }

        if (candidatesNode is not JsonArray candidates) {
            throw new InvalidDataException(
                "Gemini streamGenerateContent response field 'candidates' must be an array or null."
            );
        }

        if (candidates.Count == 0) {
            if (promptBlock is not null) {
                HandlePromptBlock(promptBlock, aggregator);
            }
            return;
        }

        if (promptBlock is not null) {
            throw new InvalidDataException(
                "Gemini streamGenerateContent response cannot contain both candidates and promptFeedback.blockReason."
            );
        }

        if (candidates.Count != 1) {
            // GeminiMessageConverter does not request candidateCount, whose
            // provider default is one. Mixing multiple candidate streams
            // would manufacture a single ActionMessage from distinct choices.
            throw new InvalidDataException(
                $"Gemini streamGenerateContent supports exactly one candidate, but received {candidates.Count}."
            );
        }

        if (candidates[0] is not JsonObject candidate) {
            throw new InvalidDataException(
                "Gemini streamGenerateContent candidate must be a JSON object."
            );
        }

        HandleCandidate(candidate, aggregator);
    }

    public void Complete(CompletionAggregator aggregator) {
        _ = aggregator;
        CompletionStreamTermination.RequireTerminalEvent(
            _terminalEventObserved,
            "Gemini streamGenerateContent"
        );
    }

    public void DiscardIncompleteStreamingState() {
        _replayParts.Clear();
    }

    private void HandleCandidate(JsonObject candidate, CompletionAggregator aggregator) {
        if (candidate.TryGetPropertyValue("index", out var indexNode)
            && indexNode is not null) {
            if (indexNode is not JsonValue indexValue
                || !indexValue.TryGetValue<int>(out var index)
                || index != 0) {
                throw new InvalidDataException(
                    "Gemini streamGenerateContent requires the single candidate index to be 0."
                );
            }
        }

        if (candidate.TryGetPropertyValue("content", out var contentNode)
            && contentNode is not null) {
            if (contentNode is not JsonObject content) {
                throw new InvalidDataException(
                    "Gemini candidate field 'content' must be an object or null."
                );
            }

            HandleContent(content, aggregator);
        }

        if (candidate.TryGetPropertyValue("finishReason", out var finishReasonNode)
            && finishReasonNode is not null) {
            var finishReason = GetRequiredString(candidate, "finishReason", "candidate");
            EmitReplayBlockIfNeeded(aggregator);
            RecordTermination(finishReason, aggregator);
            _terminalEventObserved = true;
        }
    }

    private void HandleContent(JsonObject content, CompletionAggregator aggregator) {
        _ = GetOptionalString(content, "role", "candidate content");

        if (!content.TryGetPropertyValue("parts", out var partsNode)
            || partsNode is null) {
            return;
        }
        if (partsNode is not JsonArray parts) {
            throw new InvalidDataException(
                "Gemini candidate content field 'parts' must be an array or null."
            );
        }

        foreach (var partNode in parts) {
            if (partNode is not JsonObject part) {
                throw new InvalidDataException(
                    "Gemini candidate content part must be a JSON object."
                );
            }

            HandlePart(part, aggregator);
        }
    }

    private void HandlePart(JsonObject part, CompletionAggregator aggregator) {
        var thoughtSignature = GetOptionalString(part, "thoughtSignature", "content part");
        var hasFunctionCall = part.TryGetPropertyValue("functionCall", out var functionCallNode)
            && functionCallNode is not null;
        var hasText = part.TryGetPropertyValue("text", out var textNode)
            && textNode is not null;

        if (hasFunctionCall && hasText) {
            throw new InvalidDataException(
                "Gemini content part cannot contain both 'text' and 'functionCall'."
            );
        }

        if (part.TryGetPropertyValue("functionCall", out functionCallNode)) {
            if (functionCallNode is null) {
                throw new InvalidDataException(
                    "Gemini content part field 'functionCall' must be an object."
                );
            }
            if (functionCallNode is not JsonObject functionCall) {
                throw new InvalidDataException(
                    "Gemini content part field 'functionCall' must be an object."
                );
            }

            var toolName = GetRequiredString(functionCall, "name", "functionCall");
            var toolCallId = GetOptionalString(functionCall, "id", "functionCall");
            if (string.IsNullOrWhiteSpace(toolCallId)) {
                toolCallId = $"gemini-call-{_replayParts.Count}";
            }

            var arguments = GetRequiredObject(functionCall, "args", "functionCall");
            var rawArgumentsJson = arguments.ToJsonString();
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

        if (part.TryGetPropertyValue("text", out textNode)) {
            if (textNode is not JsonValue textValue
                || !textValue.TryGetValue<string>(out var text)) {
                throw new InvalidDataException(
                    "Gemini content part field 'text' must be a string."
                );
            }
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

    private void HandleError(JsonNode? errorNode, CompletionAggregator aggregator) {
        if (errorNode is not JsonObject error) {
            throw new InvalidDataException(
                "Gemini streamGenerateContent error envelope must be a JSON object."
            );
        }

        var errorMessage = GetRequiredString(error, "message", "error envelope");
        var errorStatus = GetOptionalString(error, "status", "error envelope");
        if (error.TryGetPropertyValue("code", out var codeNode)
            && codeNode is not null
            && (codeNode is not JsonValue codeValue
                || !codeValue.TryGetValue<int>(out _))) {
            throw new InvalidDataException(
                "Gemini error envelope field 'code' must be an integer or null."
            );
        }

        EmitReplayBlockIfNeeded(aggregator);
        DebugUtil.Warning(DebugCategory, $"[Gemini] API error: {errorMessage}");
        aggregator.AppendError(errorMessage);
        aggregator.MarkFailed(errorStatus ?? "error", errorMessage);
        _terminalEventObserved = true;
    }

    private void HandlePromptBlock(
        string promptBlockReason,
        CompletionAggregator aggregator
    ) {
        EmitReplayBlockIfNeeded(aggregator);
        aggregator.MarkIncomplete(
            promptBlockReason,
            $"Gemini blocked the prompt: {promptBlockReason}."
        );
        _terminalEventObserved = true;
    }

    private static string? GetPromptBlock(JsonObject obj) {
        if (!obj.TryGetPropertyValue("promptFeedback", out var feedbackNode)
            || feedbackNode is null) {
            return null;
        }
        if (feedbackNode is not JsonObject feedback) {
            throw new InvalidDataException(
                "Gemini streamGenerateContent field 'promptFeedback' must be an object or null."
            );
        }

        var reason = GetOptionalString(feedback, "blockReason", "promptFeedback");
        return string.IsNullOrWhiteSpace(reason) ? null : reason;
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

    private static JsonObject GetRequiredObject(
        JsonObject obj,
        string propertyName,
        string context
    ) {
        if (obj[propertyName] is JsonObject value) { return value; }

        throw new InvalidDataException(
            $"Gemini {context} requires object field '{propertyName}'."
        );
    }

    private static string GetRequiredString(
        JsonObject obj,
        string propertyName,
        string context
    ) {
        if (obj[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result)) {
            return result;
        }

        throw new InvalidDataException(
            $"Gemini {context} requires non-blank string field '{propertyName}'."
        );
    }

    private static string? GetOptionalString(
        JsonObject obj,
        string propertyName,
        string context
    ) {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null) {
            return null;
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var result)) {
            return result;
        }

        throw new InvalidDataException(
            $"Gemini {context} field '{propertyName}' must be a string or null."
        );
    }
}
