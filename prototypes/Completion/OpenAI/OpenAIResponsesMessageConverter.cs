using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

internal static class OpenAIResponsesMessageConverter {
    private const string DebugCategory = "Provider";
    private const string EncryptedReasoningInclude = "reasoning.encrypted_content";
    private const string ResponsesApiSpecId = "openai-responses-v1";

    public static OpenAIResponsesApiRequest ConvertToApiRequest(
        CompletionRequest request,
        OpenAIResponsesClientOptions? options = null
    ) {
        ArgumentNullException.ThrowIfNull(request);

        options ??= new OpenAIResponsesClientOptions();

        var inputItems = new List<OpenAIResponsesInputItem>();
        var state = new ProjectionState();

        foreach (var contextMessage in request.Context) {
            switch (contextMessage) {
                case ToolResultsMessage toolResults:
                    BuildToolResultsItems(toolResults, inputItems, state);
                    break;

                case ObservationMessage observation:
                    BuildObservationItem(observation, inputItems, state);
                    break;

                case ActionMessage action:
                    BuildActionItems(action, inputItems, state);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported history message {DescribeHistoryMessage(contextMessage)}."
                    );
            }
        }

        EnsureNoPendingToolCalls(state, "context ended");

        var apiRequest = new OpenAIResponsesApiRequest {
            Model = request.ModelId,
            Instructions = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt,
            Input = inputItems,
            Tools = BuildToolDefinitions(request.Tools),
            Stream = true,
            Store = options.Store,
            Include = options.IncludeEncryptedReasoning ? [EncryptedReasoningInclude] : null,
            ParallelToolCalls = options.ParallelToolCalls
        };

        DebugUtil.Info(
            DebugCategory,
            $"[OpenAIResponses] Converted {request.Context.Count} context messages to {inputItems.Count} input items, tools={apiRequest.Tools?.Count ?? 0}"
        );

        return apiRequest;
    }

    private static void BuildObservationItem(
        ObservationMessage observation,
        List<OpenAIResponsesInputItem> inputItems,
        ProjectionState state
    ) {
        EnsureNoPendingToolCalls(state, $"observation before tool results content={observation.Content}");

        if (string.IsNullOrWhiteSpace(observation.Content)) { return; }

        inputItems.Add(CreateUserMessageItem(observation.Content));
    }

    private static void BuildToolResultsItems(
        ToolResultsMessage toolResults,
        List<OpenAIResponsesInputItem> inputItems,
        ProjectionState state
    ) {
        if (state.PendingToolCalls.Count > 0) {
            var resultsByCallId = CreateResultLookup(toolResults.Results);

            foreach (var pendingToolCall in state.PendingToolCalls) {
                if (resultsByCallId.Remove(pendingToolCall.ToolCallId, out var result)) {
                    EnsureMatchingToolName(result, pendingToolCall);
                    inputItems.Add(
                        new OpenAIResponsesFunctionCallOutputItem {
                            CallId = result.ToolCallId,
                            Output = result.GetFlattenedText()
                        }
                    );
                    continue;
                }

                throw new InvalidOperationException(
                    $"Tool results are missing for pending function call call_id='{pendingToolCall.ToolCallId}'. ToolResultsMessage.Results must align 1:1 with the pending function_call items."
                );
            }

            if (resultsByCallId.Count > 0) {
                var unexpectedCallId = resultsByCallId.Keys.First();
                throw new InvalidOperationException(
                    $"Tool result call_id='{unexpectedCallId}' does not match the pending function_call items."
                );
            }

            state.ClearPendingToolCalls();
        }
        else if (toolResults.Results.Count > 0) {
            throw new InvalidOperationException("Tool results appeared without a preceding function_call item.");
        }

        if (!string.IsNullOrWhiteSpace(toolResults.Content)) {
            inputItems.Add(CreateUserMessageItem(toolResults.Content));
        }
    }

    private static void BuildActionItems(
        ActionMessage action,
        List<OpenAIResponsesInputItem> inputItems,
        ProjectionState state
    ) {
        EnsureNoPendingToolCalls(state, $"assistant action before tool results blockCount={action.Blocks.Count}");

        var emittedItemCount = 0;
        var pendingToolCalls = new List<PendingToolCall>();
        var textBuffer = new StringBuilder();

        void FlushAssistantText() {
            if (textBuffer.Length == 0) { return; }

            inputItems.Add(
                new OpenAIResponsesMessageItem {
                    Role = "assistant",
                    Content = [
                        new OpenAIResponsesOutputTextContentItem {
                            Text = textBuffer.ToString()
                        }
                    ]
                }
            );
            emittedItemCount++;
            textBuffer.Clear();
        }

        foreach (var block in action.Blocks) {
            switch (block) {
                case ActionBlock.Text textBlock:
                    textBuffer.Append(textBlock.Content);
                    break;

                case ActionBlock.ToolCall toolCallBlock:
                    FlushAssistantText();
                    inputItems.Add(
                        new OpenAIResponsesFunctionCallItem {
                            CallId = toolCallBlock.Call.ToolCallId,
                            Name = toolCallBlock.Call.ToolName,
                            Arguments = StreamParserToolUtility.NormalizeRawArgumentsJson(toolCallBlock.Call.RawArgumentsJson)
                        }
                    );
                    pendingToolCalls.Add(new PendingToolCall(toolCallBlock.Call.ToolName, toolCallBlock.Call.ToolCallId));
                    emittedItemCount++;
                    break;

                case OpenAIResponsesReasoningBlock reasoningBlock:
                    FlushAssistantText();
                    inputItems.Add(ConvertReasoningBlock(reasoningBlock));
                    emittedItemCount++;
                    break;

                case ActionBlock.ReasoningBlock reasoningBlock:
                    throw new InvalidOperationException(
                        $"OpenAI Responses replay only supports {nameof(OpenAIResponsesReasoningBlock)}. Cross-provider reasoning replay is not supported (got '{reasoningBlock.GetType().Name}')."
                    );

                default:
                    throw new InvalidOperationException($"Unsupported action block kind {DescribeActionBlock(block)} for OpenAI Responses projection.");
            }
        }

        FlushAssistantText();

        if (emittedItemCount == 0) {
            throw new InvalidOperationException(
                "Action message has no replayable text, reasoning, or tool call content; nothing to send as assistant turn."
            );
        }

        state.SetPendingToolCalls(pendingToolCalls);
    }

    private static Dictionary<string, ToolResult> CreateResultLookup(IReadOnlyList<ToolResult> results) {
        var lookup = new Dictionary<string, ToolResult>(StringComparer.Ordinal);

        foreach (var result in results) {
            if (string.IsNullOrWhiteSpace(result.ToolCallId)) {
                throw new InvalidOperationException("Tool result is missing call_id.");
            }

            if (!lookup.TryAdd(result.ToolCallId, result)) {
                throw new InvalidOperationException($"Duplicate tool result call_id='{result.ToolCallId}'.");
            }
        }

        return lookup;
    }

    private static OpenAIResponsesMessageItem CreateUserMessageItem(string text) {
        return new OpenAIResponsesMessageItem {
            Role = "user",
            Content = [
                new OpenAIResponsesInputTextContentItem {
                    Text = text
                }
            ]
        };
    }

    private static OpenAIResponsesReasoningItem ConvertReasoningBlock(OpenAIResponsesReasoningBlock reasoningBlock) {
        if (!string.Equals(reasoningBlock.Origin.ApiSpecId, ResponsesApiSpecId, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"OpenAI Responses reasoning replay requires Origin.ApiSpecId='{ResponsesApiSpecId}', got '{reasoningBlock.Origin.ApiSpecId}'."
            );
        }

        try {
            using var document = JsonDocument.Parse(reasoningBlock.RawItemJson);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object) {
                throw new InvalidOperationException(
                    $"OpenAI Responses reasoning replay expected a JSON object payload, but got '{reasoningBlock.RawItemJson}'."
                );
            }

            var itemType = root.GetProperty("type").GetString();
            if (!string.Equals(itemType, "reasoning", StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"OpenAI Responses reasoning replay expected a reasoning item payload, but got '{reasoningBlock.RawItemJson}'."
                );
            }

            var extensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject()) {
                if (string.Equals(property.Name, "type", StringComparison.Ordinal)) { continue; }
                extensionData[property.Name] = property.Value.Clone();
            }

            return new OpenAIResponsesReasoningItem {
                ExtensionData = extensionData
            };
        }
        catch (JsonException ex) {
            throw new InvalidOperationException(
                "OpenAI Responses reasoning replay payload is not valid JSON.",
                ex
            );
        }
    }

    private static List<OpenAIResponsesTool>? BuildToolDefinitions(ImmutableArray<ToolDefinition> tools) {
        if (tools.IsDefaultOrEmpty) { return null; }

        var list = new List<OpenAIResponsesTool>(tools.Length);
        foreach (var definition in tools) {
            list.Add(
                new OpenAIResponsesTool {
                    Name = definition.Name,
                    Description = definition.Description,
                    Parameters = JsonToolSchemaBuilder.BuildSchema(definition),
                    Strict = true
                }
            );
        }

        return list;
    }

    private static void EnsureMatchingToolName(ToolResult result, PendingToolCall pendingToolCall) {
        if (string.Equals(result.ToolName, pendingToolCall.ToolName, StringComparison.Ordinal)) { return; }

        throw new InvalidOperationException(
            $"OpenAI Responses tool result name mismatch for call_id='{pendingToolCall.ToolCallId}': expected '{pendingToolCall.ToolName}', got '{result.ToolName}'. ToolResultsMessage.Results must align by ToolCallId + ToolName."
        );
    }

    private static void EnsureNoPendingToolCalls(ProjectionState state, string nextContextDescription) {
        if (state.PendingToolCalls.Count == 0) { return; }

        var pendingIds = string.Join(", ", state.PendingToolCalls.Select(static call => call.ToolCallId));
        throw new InvalidOperationException(
            $"Pending function_call items must be followed immediately by tool results before {nextContextDescription}. pending=[{pendingIds}]"
        );
    }

    private static string DescribeHistoryMessage(IHistoryMessage? message)
        => message is null ? "<null>" : $"type '{message.GetType().Name}' with Kind={message.Kind}";

    private static string DescribeActionBlock(ActionBlock? block)
        => block is null ? "<null>" : $"'{block.Kind}'";

    private sealed class ProjectionState {
        private List<PendingToolCall> _pendingToolCalls = [];

        public IReadOnlyList<PendingToolCall> PendingToolCalls => _pendingToolCalls;

        public void ClearPendingToolCalls() {
            _pendingToolCalls.Clear();
        }

        public void SetPendingToolCalls(List<PendingToolCall> pendingToolCalls) {
            _pendingToolCalls = pendingToolCalls;
        }
    }

    private sealed record PendingToolCall(string ToolName, string ToolCallId);
}
