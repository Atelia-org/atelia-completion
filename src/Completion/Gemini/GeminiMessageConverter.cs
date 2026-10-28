using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.Gemini;

internal static class GeminiMessageConverter {
    public static GeminiGenerateContentRequest ConvertToApiRequest(
        CompletionRequest request,
        int modelMaximumTokens,
        CompletionDescriptor targetInvocation
    ) {
        CompletionToolInputValidation.RequireJsonTools(request);
        ArgumentNullException.ThrowIfNull(targetInvocation);
        if (modelMaximumTokens <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(modelMaximumTokens),
                modelMaximumTokens,
                "Provider-resolved model maximum must be positive."
            );
        }
        var apiRequest = ProjectRequest(request, targetInvocation);
        apiRequest.GenerationConfig = new GeminiGenerationConfig {
            MaxOutputTokens = modelMaximumTokens
        };
        return apiRequest;
    }

    // Project and validate before capability I/O, without a placeholder output limit.
    internal static GeminiGenerateContentRequest ProjectRequest(
        CompletionRequest request,
        CompletionDescriptor targetInvocation
    ) {
        ArgumentNullException.ThrowIfNull(targetInvocation);
        var contents = new List<GeminiContent>();
        var pendingToolCalls = new List<PendingToolCall>();

        ProjectMessages(
            request.PromptPrefix.SharedContextMessages,
            contents,
            pendingToolCalls,
            targetInvocation
        );
        ProjectMessages(
            request.TailMessages,
            contents,
            pendingToolCalls,
            targetInvocation
        );

        CompletionOutputContract outputContract =
            request.PromptPrefix.OutputContract;
        var apiRequest = new GeminiGenerateContentRequest {
            Contents = contents,
            SystemInstruction = string.IsNullOrWhiteSpace(
                request.PromptPrefix.SystemPrompt
            )
                ? null
                : new GeminiContent {
                    Parts = new List<GeminiPart> {
                        new() { Text = request.PromptPrefix.SystemPrompt }
                    }
                },
            Tools = BuildToolDefinitions(outputContract.Tools),
            ToolConfig = BuildToolConfig(outputContract)
        };

        int contextMessageCount = checked(
            request.PromptPrefix.SharedContextMessages.Length
                + request.TailMessages.Length
        );
        DebugUtil.Debug(
            CompletionDebugCategories.Provider,
            $"[Gemini] Converted {contextMessageCount} context messages to {contents.Count} contents, tools={apiRequest.Tools?.Count ?? 0}"
        );

        return apiRequest;
    }

    private static void ProjectMessages(
        IReadOnlyList<IHistoryMessage> contextMessages,
        List<GeminiContent> contents,
        List<PendingToolCall> pendingToolCalls,
        CompletionDescriptor targetInvocation
    ) {
        foreach (IHistoryMessage contextMessage in contextMessages) {
            switch (contextMessage) {
                case ToolResultsMessage toolResults:
                    BuildToolResultsContent(toolResults, contents, pendingToolCalls);
                    break;

                case ObservationMessage observation:
                    BuildObservationContent(observation, contents, pendingToolCalls);
                    break;

                case ActionMessage action:
                    BuildActionContent(action, contents, pendingToolCalls, targetInvocation);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported history message type '{contextMessage.GetType().Name}' with Kind={contextMessage.Kind}."
                    );
            }
        }
    }

    private static GeminiToolConfig? BuildToolConfig(
        CompletionOutputContract outputContract
    ) {
        if (outputContract.AllowParallelToolCalls is false) {
            throw new NotSupportedException(
                "Gemini function-calling config cannot guarantee that parallel tool calls are disabled."
            );
        }

        CompletionToolChoice toolChoice = outputContract.ToolChoice;
        if (toolChoice.Kind is CompletionToolChoiceKind.ProviderDefault) {
            return null;
        }

        return new GeminiToolConfig {
            FunctionCallingConfig = new GeminiFunctionCallingConfig {
                Mode = toolChoice.Kind switch {
                    CompletionToolChoiceKind.Auto => "AUTO",
                    CompletionToolChoiceKind.None => "NONE",
                    CompletionToolChoiceKind.RequiredAny => "ANY",
                    CompletionToolChoiceKind.RequiredNamed => "ANY",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(outputContract),
                        toolChoice.Kind,
                        "Unknown completion tool choice."
                    )
                },
                AllowedFunctionNames = toolChoice.Kind
                    is CompletionToolChoiceKind.RequiredNamed
                        ? [toolChoice.RequiredToolName
                            ?? throw new InvalidOperationException(
                                "RequiredNamed tool choice is missing its tool name."
                            )]
                        : null
            }
        };
    }

    private static void BuildObservationContent(
        ObservationMessage observation,
        List<GeminiContent> contents,
        List<PendingToolCall> pendingToolCalls
    ) {
        if (string.IsNullOrWhiteSpace(observation.Content)) { return; }
        pendingToolCalls.Clear();

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

        // Keep established call-order projection for an unambiguous association.
        // Otherwise preserve supplied history and let the endpoint validate it.
        IReadOnlyList<ToolResult> orderedResults = toolResults.Results;
        if (pendingToolCalls.Count == toolResults.Results.Count
            && pendingToolCalls.Select(c => c.ToolCallId).Distinct(StringComparer.Ordinal).Count() == pendingToolCalls.Count
            && toolResults.Results.Select(r => r.ToolCallId).Distinct(StringComparer.Ordinal).Count() == toolResults.Results.Count) {
            var lookup = toolResults.Results.ToDictionary(r => r.ToolCallId, StringComparer.Ordinal);
            if (pendingToolCalls.All(c => lookup.TryGetValue(c.ToolCallId, out var result)
                && string.Equals(c.ToolName, result.ToolName, StringComparison.Ordinal))) {
                orderedResults = pendingToolCalls.Select(c => lookup[c.ToolCallId]).ToArray();
            }
        }
        foreach (var result in orderedResults) parts.Add(CreateFunctionResponsePart(result));
        pendingToolCalls.Clear();

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
        List<PendingToolCall> pendingToolCalls,
        CompletionDescriptor targetInvocation
    ) {
        pendingToolCalls.Clear();

        var replayBlock = action.Blocks.OfType<GeminiReplayBlock>().SingleOrDefault();
        if (replayBlock is not null
            && string.Equals(replayBlock.Origin.ProviderId, targetInvocation.ProviderId, StringComparison.Ordinal)
            && string.Equals(replayBlock.Origin.ApiSpecId, targetInvocation.ApiSpecId, StringComparison.Ordinal)) {
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

                case ActionBlock.ToolCall toolCallBlock:
                    var toolCall = toolCallBlock.Call;
                    parts.Add(new GeminiPart {
                        FunctionCall = new GeminiFunctionCall {
                            Name = toolCall.ToolName,
                            Id = toolCall.ToolCallId,
                            Args = BuildFunctionArgs(toolCall.RawArgumentsJson)
                        },
                        // Google's documented import path for tool history from other models.
                        // Set it on the first call of each action, so appending a new turn
                        // does not rewrite the already projected history prefix.
                        // https://ai.google.dev/gemini-api/docs/generate-content/thought-signatures
                        ThoughtSignature = hasToolCalls ? null : "skip_thought_signature_validator"
                    });
                    pendingToolCalls.Add(new PendingToolCall(toolCall.ToolName, toolCall.ToolCallId));
                    hasToolCalls = true;
                    break;

                case ActionBlock.ReasoningBlock:
                    break;
            }
        }

        if (parts.Count == 0) {
            throw new InvalidOperationException(
                "Action message has no Gemini-replayable content. Provide visible text or tool calls, or a GeminiReplayBlock matching the target provider and API spec."
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
                    Args = BuildFunctionArgs(payloadPart.FunctionCall.RawArgumentsJson)
                };

                if (payloadPart.FunctionCall.ToolCallId is { } callId) {
                    pendingToolCalls.Add(new PendingToolCall(payloadPart.FunctionCall.Name, callId));
                }
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
            .Select(static part => part.FunctionCall!)
            .ToArray();

        if (payloadToolCalls.Length != visibleToolCalls.Count) {
            throw new InvalidOperationException(
                "Gemini replay payload tool calls do not match the ActionMessage tool-call blocks. Refuse to pick between two sources of truth."
            );
        }

        for (var i = 0; i < payloadToolCalls.Length; i++) {
            var payloadCall = payloadToolCalls[i];
            var visibleCall = visibleToolCalls[i];

            if (!string.Equals(payloadCall.Name, visibleCall.ToolName, StringComparison.Ordinal)
                || !string.Equals(payloadCall.ToolCallId, visibleCall.ToolCallId, StringComparison.Ordinal)
                || !JsonTextsSemanticallyEqual(payloadCall.RawArgumentsJson, visibleCall.RawArgumentsJson)) {
                throw new InvalidOperationException(
                    "Gemini replay payload tool calls do not match the ActionMessage tool-call blocks. Refuse to pick between two sources of truth."
                );
            }
        }
    }

    private static JsonElement BuildFunctionArgs(string rawArgumentsJson) {
        // Gemini functionCall.args is an object. Never silently replace invalid
        // arguments with {}, which would describe a different tool invocation.
        using var document = JsonDocument.Parse(rawArgumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) {
            throw new InvalidOperationException("Gemini functionCall args must be a JSON object.");
        }
        return document.RootElement.Clone();
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

    private static List<GeminiTool>? BuildToolDefinitions(ImmutableArray<ToolDefinition> tools) {
        if (tools.IsDefaultOrEmpty) { return null; }

        var declarations = new List<GeminiFunctionDeclaration>(tools.Length);
        foreach (var definition in tools) {
            declarations.Add(
                new GeminiFunctionDeclaration {
                    Name = definition.Name,
                    Description = definition.Description,
                    ParametersJsonSchema = JsonToolSchemaBuilder.BuildSchema(definition)
                }
            );
        }

        return new List<GeminiTool> {
            new() {
                FunctionDeclarations = declarations
            }
        };
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
