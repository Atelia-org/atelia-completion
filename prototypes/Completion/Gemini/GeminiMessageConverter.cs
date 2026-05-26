using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.Gemini;

internal static class GeminiMessageConverter {
    private const string DebugCategory = "Provider";

    public static GeminiGenerateContentRequest ConvertToApiRequest(CompletionRequest request) {
        var contents = new List<GeminiContent>();
        var pendingToolCalls = new List<PendingToolCall>();

        foreach (var contextMessage in request.Context) {
            switch (contextMessage) {
                case ToolResultsMessage toolResults:
                    BuildToolResultsContent(toolResults, contents, pendingToolCalls);
                    break;

                case ObservationMessage observation:
                    BuildObservationContent(observation, contents, pendingToolCalls);
                    break;

                case ActionMessage action:
                    BuildActionContent(action, contents, pendingToolCalls);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported history message type '{contextMessage.GetType().Name}' with Kind={contextMessage.Kind}."
                    );
            }
        }

        EnsureNoPendingToolCalls(pendingToolCalls, "context ended");

        var apiRequest = new GeminiGenerateContentRequest {
            Contents = contents,
            SystemInstruction = string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? null
                : new GeminiContent {
                    Parts = new List<GeminiPart> {
                        new() { Text = request.SystemPrompt }
                    }
                },
            Tools = BuildToolDefinitions(request.Tools)
        };

        DebugUtil.Info(
            DebugCategory,
            $"[Gemini] Converted {request.Context.Count} context messages to {contents.Count} contents, tools={apiRequest.Tools?.Count ?? 0}"
        );

        return apiRequest;
    }

    private static void BuildObservationContent(
        ObservationMessage observation,
        List<GeminiContent> contents,
        List<PendingToolCall> pendingToolCalls
    ) {
        EnsureNoPendingToolCalls(pendingToolCalls, $"observation before tool results content={observation.Content}");

        if (string.IsNullOrWhiteSpace(observation.Content)) { return; }

        contents.Add(
            new GeminiContent {
                Role = "user",
                Parts = new List<GeminiPart> {
                    new() { Text = observation.Content }
                }
            }
        );
    }

    private static void BuildToolResultsContent(
        ToolResultsMessage toolResults,
        List<GeminiContent> contents,
        List<PendingToolCall> pendingToolCalls
    ) {
        var parts = new List<GeminiPart>();

        if (pendingToolCalls.Count > 0) {
            var resultsByCallId = CreateResultLookup(toolResults.Results);

            foreach (var pendingToolCall in pendingToolCalls) {
                if (resultsByCallId.Remove(pendingToolCall.ToolCallId, out var result)) {
                    parts.Add(CreateFunctionResponsePart(result));
                    continue;
                }

                throw new InvalidOperationException(
                    $"Tool results are missing for pending Gemini functionCall id='{pendingToolCall.ToolCallId}'. ToolResultsMessage.Results must align 1:1 with the pending functionCall parts."
                );
            }

            if (resultsByCallId.Count > 0) {
                var unexpectedCallId = resultsByCallId.Keys.First();
                throw new InvalidOperationException(
                    $"Tool result id='{unexpectedCallId}' does not match the pending Gemini functionCall parts."
                );
            }

            pendingToolCalls.Clear();
        }
        else if (toolResults.Results.Count > 0) { throw new InvalidOperationException("Tool results appeared without a preceding Gemini functionCall content."); }

        AppendTrailingObservation(toolResults, parts);

        if (parts.Count == 0) { return; }

        contents.Add(
            new GeminiContent {
                Role = "user",
                Parts = parts
            }
        );
    }

    private static void BuildActionContent(
        ActionMessage action,
        List<GeminiContent> contents,
        List<PendingToolCall> pendingToolCalls
    ) {
        EnsureNoPendingToolCalls(pendingToolCalls, $"model action before tool results blockCount={action.Blocks.Count}");

        var replayBlock = action.Blocks.OfType<GeminiReplayBlock>().SingleOrDefault();
        if (replayBlock is not null) {
            ValidateReplayBlockConsistency(action, replayBlock);
            var replayContent = BuildContentFromReplayBlock(replayBlock, pendingToolCalls);
            contents.Add(replayContent);
            return;
        }

        var parts = new List<GeminiPart>();
        var hasToolCalls = false;

        foreach (var block in action.Blocks) {
            switch (block) {
                case ActionBlock.Text textBlock when !string.IsNullOrWhiteSpace(textBlock.Content):
                    parts.Add(new GeminiPart { Text = textBlock.Content });
                    break;

                case ActionBlock.ToolCall:
                    hasToolCalls = true;
                    break;

                case ActionBlock.ReasoningBlock:
                    break;
            }
        }

        if (hasToolCalls) {
            throw new InvalidOperationException(
                "Gemini tool replay requires GeminiReplayBlock so functionCall thoughtSignature can be preserved."
            );
        }

        if (parts.Count == 0) {
            throw new InvalidOperationException(
                "Action message has no Gemini-replayable content. Provide text content or a GeminiReplayBlock."
            );
        }

        contents.Add(
            new GeminiContent {
                Role = "model",
                Parts = parts
            }
        );
    }

    private static GeminiContent BuildContentFromReplayBlock(
        GeminiReplayBlock replayBlock,
        List<PendingToolCall> pendingToolCalls
    ) {
        var payload = GeminiReplayPayloadCodec.Decode(replayBlock.OpaquePayload);
        if (!string.Equals(payload.Role, "model", StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"Gemini replay payload role must be 'model' but was '{payload.Role}'."
            );
        }

        var parts = new List<GeminiPart>(payload.Parts.Count);
        foreach (var payloadPart in payload.Parts) {
            var part = new GeminiPart {
                Text = payloadPart.Text,
                ThoughtSignature = payloadPart.ThoughtSignature
            };

            if (payloadPart.FunctionCall is not null) {
                part.FunctionCall = new GeminiFunctionCall {
                    Name = payloadPart.FunctionCall.Name,
                    Id = payloadPart.FunctionCall.ToolCallId,
                    Args = BuildFunctionArgs(payloadPart.FunctionCall)
                };

                pendingToolCalls.Add(new PendingToolCall(payloadPart.FunctionCall.Name, payloadPart.FunctionCall.ToolCallId));
            }

            parts.Add(part);
        }

        if (parts.Count == 0) { throw new InvalidOperationException("Gemini replay payload contained no parts."); }

        return new GeminiContent {
            Role = payload.Role,
            Parts = parts
        };
    }

    private static void ValidateReplayBlockConsistency(ActionMessage action, GeminiReplayBlock replayBlock) {
        var payload = GeminiReplayPayloadCodec.Decode(replayBlock.OpaquePayload);
        var payloadText = string.Concat(payload.Parts.Select(static part => part.Text ?? string.Empty));
        var visibleText = action.GetFlattenedText();
        var visibleToolCalls = action.ToolCalls;

        if (visibleText.Length == 0 && visibleToolCalls.Count == 0) { return; }

        if (!string.Equals(payloadText, visibleText, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                "Gemini replay payload text does not match the ActionMessage text blocks. Refuse to pick between two sources of truth."
            );
        }

        var payloadToolCalls = payload.Parts
            .Where(static part => part.FunctionCall is not null)
            .Select(
            static part => new RawToolCall(
                part.FunctionCall!.Name,
                part.FunctionCall.ToolCallId,
                StreamParserToolUtility.NormalizeRawArgumentsJson(part.FunctionCall.RawArgumentsJson)
            )
        )
            .ToArray();

        if (payloadToolCalls.Length != visibleToolCalls.Count) {
            throw new InvalidOperationException(
                "Gemini replay payload tool calls do not match the ActionMessage tool-call blocks. Refuse to pick between two sources of truth."
            );
        }

        for (var i = 0; i < payloadToolCalls.Length; i++) {
            var payloadCall = payloadToolCalls[i];
            var visibleCall = visibleToolCalls[i];

            if (!string.Equals(payloadCall.ToolName, visibleCall.ToolName, StringComparison.Ordinal)
                || !string.Equals(payloadCall.ToolCallId, visibleCall.ToolCallId, StringComparison.Ordinal)
                || !JsonTextsSemanticallyEqual(payloadCall.RawArgumentsJson, visibleCall.RawArgumentsJson)) {
                throw new InvalidOperationException(
                    "Gemini replay payload tool calls do not match the ActionMessage tool-call blocks. Refuse to pick between two sources of truth."
                );
            }
        }
    }

    private static JsonElement BuildFunctionArgs(GeminiReplayPayloadCodec.GeminiReplayPayloadFunctionCall functionCall) {
        var json = StreamParserToolUtility.NormalizeRawArgumentsJson(functionCall.RawArgumentsJson);

        try {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object) { return document.RootElement.Clone(); }

            DebugUtil.Warning(
                DebugCategory,
                $"[Gemini] Function replay requires object args; fallback to empty object toolName={functionCall.Name} toolCallId={functionCall.ToolCallId} rootKind={document.RootElement.ValueKind}"
            );
        }
        catch (JsonException ex) {
            DebugUtil.Warning(
                DebugCategory,
                $"[Gemini] Function replay received invalid raw args JSON; fallback to empty object toolName={functionCall.Name} toolCallId={functionCall.ToolCallId} error={ex.Message}"
            );
        }

        return JsonSerializer.SerializeToElement(new JsonObject());
    }

    private static GeminiPart CreateFunctionResponsePart(ToolResult result) {
        return new GeminiPart {
            FunctionResponse = new GeminiFunctionResponse {
                Name = result.ToolName,
                Id = result.ToolCallId,
                Response = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?> {
                        ["tool_name"] = result.ToolName,
                        ["status"] = result.Status.ToString().ToLowerInvariant(),
                        ["result"] = result.GetFlattenedText()
                    }
                )
            }
        };
    }

    private static void AppendTrailingObservation(ToolResultsMessage toolResults, List<GeminiPart> parts) {
        if (!string.IsNullOrWhiteSpace(toolResults.Content)) {
            parts.Add(new GeminiPart { Text = toolResults.Content });
        }
    }

    private static Dictionary<string, ToolResult> CreateResultLookup(IReadOnlyList<ToolResult> results) {
        var lookup = new Dictionary<string, ToolResult>(StringComparer.Ordinal);

        foreach (var result in results) {
            if (string.IsNullOrWhiteSpace(result.ToolCallId)) { throw new InvalidOperationException("Tool result is missing Gemini functionCall id."); }

            if (!lookup.TryAdd(result.ToolCallId, result)) { throw new InvalidOperationException($"Duplicate Gemini tool result id='{result.ToolCallId}'."); }
        }

        return lookup;
    }

    private static void EnsureNoPendingToolCalls(IReadOnlyList<PendingToolCall> pendingToolCalls, string context) {
        if (pendingToolCalls.Count == 0) { return; }

        var firstPending = pendingToolCalls[0];
        throw new InvalidOperationException(
            $"Gemini pending functionCall id='{firstPending.ToolCallId}' was not followed by ToolResultsMessage before {context}."
        );
    }

    private static List<GeminiTool>? BuildToolDefinitions(ImmutableArray<ToolDefinition> tools) {
        if (tools.IsDefaultOrEmpty) { return null; }

        var declarations = new List<GeminiFunctionDeclaration>(tools.Length);
        foreach (var definition in tools) {
            declarations.Add(
                new GeminiFunctionDeclaration {
                    Name = definition.Name,
                    Description = definition.Description,
                    Parameters = ConvertJsonSchemaToGeminiSchema(JsonToolSchemaBuilder.BuildSchema(definition))
                }
            );
        }

        return new List<GeminiTool> {
            new() {
                FunctionDeclarations = declarations
            }
        };
    }

    private static JsonElement ConvertJsonSchemaToGeminiSchema(JsonElement schema) {
        var node = JsonNode.Parse(schema.GetRawText());
        if (node is null) { return JsonSerializer.SerializeToElement(new JsonObject()); }

        RewriteJsonSchemaNode(node);
        return node.Deserialize<JsonElement>();
    }

    private static void RewriteJsonSchemaNode(JsonNode node) {
        switch (node) {
            case JsonObject obj:
                obj.Remove("additionalProperties");
                obj.Remove("examples");
                obj.Remove("format");

                if (obj["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeName)) {
                    obj["type"] = typeName switch {
                        "object" => "OBJECT",
                        "string" => "STRING",
                        "boolean" => "BOOLEAN",
                        "integer" => "INTEGER",
                        "number" => "NUMBER",
                        "array" => "ARRAY",
                        _ => typeName
                    };
                }

                foreach (var (_, child) in obj.ToList()) {
                    if (child is not null) {
                        RewriteJsonSchemaNode(child);
                    }
                }

                break;

            case JsonArray array:
                foreach (var child in array) {
                    if (child is not null) {
                        RewriteJsonSchemaNode(child);
                    }
                }

                break;
        }
    }

    private static bool JsonTextsSemanticallyEqual(string leftJson, string rightJson) {
        try {
            using var left = JsonDocument.Parse(StreamParserToolUtility.NormalizeRawArgumentsJson(leftJson));
            using var right = JsonDocument.Parse(StreamParserToolUtility.NormalizeRawArgumentsJson(rightJson));
            return JsonElementDeepEquals(left.RootElement, right.RootElement);
        }
        catch (JsonException) {
            return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
        }
    }

    private static bool JsonElementDeepEquals(JsonElement left, JsonElement right) {
        if (left.ValueKind != right.ValueKind) { return false; }

        return left.ValueKind switch {
            JsonValueKind.Object => JsonObjectDeepEquals(left, right),
            JsonValueKind.Array => JsonArrayDeepEquals(left, right),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal)
        };
    }

    private static bool JsonObjectDeepEquals(JsonElement left, JsonElement right) {
        var leftProperties = left.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
        var rightProperties = right.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();

        if (leftProperties.Length != rightProperties.Length) { return false; }

        for (var i = 0; i < leftProperties.Length; i++) {
            if (!string.Equals(leftProperties[i].Name, rightProperties[i].Name, StringComparison.Ordinal)) { return false; }

            if (!JsonElementDeepEquals(leftProperties[i].Value, rightProperties[i].Value)) { return false; }
        }

        return true;
    }

    private static bool JsonArrayDeepEquals(JsonElement left, JsonElement right) {
        var leftItems = left.EnumerateArray().ToArray();
        var rightItems = right.EnumerateArray().ToArray();

        if (leftItems.Length != rightItems.Length) { return false; }

        for (var i = 0; i < leftItems.Length; i++) {
            if (!JsonElementDeepEquals(leftItems[i], rightItems[i])) { return false; }
        }

        return true;
    }

    private sealed record PendingToolCall(string ToolName, string ToolCallId);
}
