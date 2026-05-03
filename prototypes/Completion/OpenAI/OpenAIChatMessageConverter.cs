using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

internal static class OpenAIChatMessageConverter {
    private const string DebugCategory = "Provider";

    public static OpenAIChatApiRequest ConvertToApiRequest(CompletionRequest request, OpenAIChatDialect dialect) {
        var messages = new List<OpenAIChatMessage>();
        var state = new ProjectionState();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt)) {
            messages.Add(
                new OpenAIChatMessage {
                    Role = "system",
                    Content = request.SystemPrompt
                }
            );
        }

        foreach (var contextMessage in request.Context) {
            switch (contextMessage) {
                case ToolResultsMessage toolResults:
                    BuildToolResultsMessages(toolResults, messages, dialect, state);
                    break;

                case ObservationMessage input:
                    BuildObservationMessage(input, messages, state);
                    break;

                case ActionMessage output:
                    BuildActionMessage(output, messages, state, dialect);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported history message {DescribeHistoryMessage(contextMessage)}."
                    );
            }
        }

        EnsureNoPendingToolCalls(state, "context ended");

        var apiRequest = new OpenAIChatApiRequest {
            Model = request.ModelId,
            Messages = messages,
            Stream = true,
            Tools = BuildToolDefinitions(request.Tools)
        };

        DebugUtil.Info(
            DebugCategory,
            $"[OpenAI] Converted {request.Context.Count} context messages to {messages.Count} API messages, tools={apiRequest.Tools?.Count ?? 0}, dialect={dialect.Name}"
        );

        return apiRequest;
    }

    private static void BuildObservationMessage(ObservationMessage input, List<OpenAIChatMessage> messages, ProjectionState state) {
        EnsureNoPendingToolCalls(state, $"observation before tool results content={input.Content}");

        messages.Add(
            new OpenAIChatMessage {
                Role = "user",
                Content = input.Content ?? string.Empty
            }
        );
    }

    private static void BuildToolResultsMessages(
        ToolResultsMessage toolResults,
        List<OpenAIChatMessage> messages,
        OpenAIChatDialect dialect,
        ProjectionState state
    ) {
        BuildStrictToolResultMessages(toolResults, messages, state);
    }

    private static void BuildStrictToolResultMessages(
        ToolResultsMessage toolResults,
        List<OpenAIChatMessage> messages,
        ProjectionState state
    ) {
        var consumedExecuteErrorBySyntheticToolMessages = false;

        if (state.PendingToolCalls.Count > 0) {
            var resultsByCallId = CreateResultLookup(toolResults.Results);

            foreach (var pendingToolCall in state.PendingToolCalls) {
                if (resultsByCallId.Remove(pendingToolCall.ToolCallId, out var result)) {
                    messages.Add(CreateToolResultMessage(result));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(toolResults.ExecuteError)) {
                    throw new InvalidOperationException(
                        $"Tool results are missing for pending tool_call_id='{pendingToolCall.ToolCallId}' and no execute_error was provided."
                    );
                }

                consumedExecuteErrorBySyntheticToolMessages = true;
                messages.Add(CreateToolResultMessage(CreateSyntheticFailureResult(pendingToolCall, toolResults.ExecuteError)));
            }

            if (resultsByCallId.Count > 0) {
                var unexpectedCallId = resultsByCallId.Keys.First();
                throw new InvalidOperationException(
                    $"Tool result tool_call_id='{unexpectedCallId}' does not match the pending assistant tool_calls."
                );
            }

            state.ClearPendingToolCalls();
        }
        else if (toolResults.Results.Count > 0) { throw new InvalidOperationException("Tool results appeared without a preceding assistant tool_calls message."); }

        AppendTrailingObservation(toolResults, messages, includeExecuteError: !consumedExecuteErrorBySyntheticToolMessages);
    }

    private static Dictionary<string, ToolResult> CreateResultLookup(IReadOnlyList<ToolResult> results) {
        var lookup = new Dictionary<string, ToolResult>(StringComparer.Ordinal);

        foreach (var result in results) {
            if (string.IsNullOrWhiteSpace(result.ToolCallId)) { throw new InvalidOperationException("Tool result is missing tool_call_id."); }

            if (!lookup.TryAdd(result.ToolCallId, result)) { throw new InvalidOperationException($"Duplicate tool result tool_call_id='{result.ToolCallId}'."); }
        }

        return lookup;
    }

    private static OpenAIChatMessage CreateToolResultMessage(ToolResult result) {
        return new OpenAIChatMessage {
            Role = "tool",
            ToolCallId = result.ToolCallId,
            Content = BuildToolResultContent(result)
        };
    }

    private static ToolResult CreateSyntheticFailureResult(PendingToolCall pendingToolCall, string executeError) {
        return new ToolResult(
            ToolName: pendingToolCall.ToolName,
            ToolCallId: pendingToolCall.ToolCallId,
            Status: ToolExecutionStatus.Failed,
            Result: executeError
        );
    }

    private static void AppendTrailingObservation(
        ToolResultsMessage toolResults,
        List<OpenAIChatMessage> messages,
        bool includeExecuteError
    ) {
        List<string>? trailingObservationParts = null;

        if (!string.IsNullOrWhiteSpace(toolResults.Content)) {
            trailingObservationParts = new List<string>(capacity: 2) { toolResults.Content };
        }

        if (includeExecuteError && !string.IsNullOrWhiteSpace(toolResults.ExecuteError)) {
            trailingObservationParts ??= new List<string>(capacity: 1);
            trailingObservationParts.Add($"[Execution Error]: {toolResults.ExecuteError}");
        }

        if (trailingObservationParts is { Count: > 0 }) {
            messages.Add(
                new OpenAIChatMessage {
                    Role = "user",
                    Content = string.Join('\n', trailingObservationParts)
                }
            );
        }
    }

    private static string BuildToolResultContent(ToolResult result) {
        var payload = new Dictionary<string, object?> {
            ["tool_name"] = result.ToolName,
            ["status"] = result.Status.ToString().ToLowerInvariant(),
            ["result"] = result.Result
        };

        return JsonSerializer.Serialize(payload);
    }

    private static void BuildActionMessage(
        ActionMessage output,
        List<OpenAIChatMessage> messages,
        ProjectionState state,
        OpenAIChatDialect dialect
    ) {
        EnsureNoPendingToolCalls(state, $"assistant action before tool results blockCount={output.Blocks.Count}");

        // 从 Blocks 提取：Text 块拼成 content；ToolCall 块提取为 toolCalls；
        // reasoning 仅在支持 replay 的 dialect 下投影到 reasoning_content。
        var contentBuilder = new System.Text.StringBuilder();
        var reasoningBuilder = new System.Text.StringBuilder();
        var toolCallList = new List<RawToolCall>(output.Blocks.Count);

        foreach (var block in output.Blocks) {
            switch (block) {
                case ActionBlock.Text textBlock:
                    contentBuilder.Append(textBlock.Content);
                    break;
                case ActionBlock.ToolCall toolCallBlock:
                    toolCallList.Add(toolCallBlock.Call);
                    break;
                case OpenAIChatReasoningBlock openAiReasoningBlock when dialect.ReasoningMode is OpenAIChatReasoningMode.ReplayCompatible:
                    reasoningBuilder.Append(openAiReasoningBlock.Content);
                    break;
                case ActionBlock.TextReasoningBlock textReasoningBlock when dialect.ReasoningMode is OpenAIChatReasoningMode.ReplayCompatible:
                    reasoningBuilder.Append(textReasoningBlock.Content);
                    break;
                case ActionBlock.ReasoningBlock:
                    // 默认 strict/capture-only 路径不回灌 reasoning_content，保持最保守的 OpenAI 语义。
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported action block kind {DescribeActionBlock(block)} for OpenAI projection.");
            }
        }

        var content = contentBuilder.ToString();

        if (toolCallList.Count > 0 && string.IsNullOrEmpty(content)) {
            content = null;
        }
        else if (toolCallList.Count == 0 && content.Length == 0) {
            content = string.Empty;
        }

        var toolCalls = toolCallList.Count > 0 ? BuildToolCallHistory(toolCallList) : null;
        var reasoningContent = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null;

        messages.Add(
            new OpenAIChatMessage {
                Role = "assistant",
                Content = content,
                ReasoningContent = reasoningContent,
                ToolCalls = toolCalls
            }
        );

        state.SetPendingToolCalls(toolCalls is { Count: > 0 } ? toolCalls : null);
    }

    private static string DescribeHistoryMessage(IHistoryMessage? message)
        => message is null ? "<null>" : $"type '{message.GetType().Name}' with Kind={message.Kind}";

    private static string DescribeActionBlock(ActionBlock? block)
        => block is null ? "<null>" : $"'{block.Kind}'";

    private static List<OpenAIChatToolCall>? BuildToolCallHistory(IReadOnlyList<RawToolCall> toolCalls) {
        if (toolCalls.Count == 0) { return null; }

        var list = new List<OpenAIChatToolCall>(toolCalls.Count);
        for (var i = 0; i < toolCalls.Count; i++) {
            var toolCall = toolCalls[i];
            list.Add(
                new OpenAIChatToolCall {
                    Id = string.IsNullOrWhiteSpace(toolCall.ToolCallId) ? CreateSyntheticToolCallId(toolCall.ToolName, i) : toolCall.ToolCallId,
                    Type = "function",
                    Function = new OpenAIChatFunctionCall {
                        Name = toolCall.ToolName,
                        Arguments = BuildToolCallArguments(toolCall)
                    }
                }
            );
        }

        return list;
    }

    private static string BuildToolCallArguments(RawToolCall toolCall) {
        return StreamParserToolUtility.NormalizeRawArgumentsJson(toolCall.RawArgumentsJson);
    }

    private static List<OpenAIChatTool>? BuildToolDefinitions(ImmutableArray<ToolDefinition> tools) {
        if (tools.IsDefaultOrEmpty) { return null; }

        var list = new List<OpenAIChatTool>(tools.Length);
        foreach (var definition in tools) {
            list.Add(
                new OpenAIChatTool {
                    Function = new OpenAIChatToolDefinition {
                        Name = definition.Name,
                        Description = definition.Description,
                        Parameters = JsonToolSchemaBuilder.BuildSchema(definition)
                    }
                }
            );
        }

        return list;
    }

    // 同名工具在一次 assistant turn 中可被多次调用；只用 toolName 会冲突。
    // 这里附带 index，并与 OpenAIChatStreamParser.CreateToolCall 中 "openai-call-{state.Index}" 的命名保持同前缀。
    private static string CreateSyntheticToolCallId(string toolName, int index) {
        var safeName = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName;
        return $"openai-call-{safeName}-{index}";
    }

    private static void EnsureNoPendingToolCalls(ProjectionState state, string nextContextDescription) {
        if (state.PendingToolCalls.Count == 0) { return; }

        var pendingIds = string.Join(", ", state.PendingToolCalls.Select(static call => call.ToolCallId));
        throw new InvalidOperationException(
            $"Pending assistant tool_calls must be followed immediately by tool results before {nextContextDescription}. pending=[{pendingIds}]"
        );
    }

    private sealed class ProjectionState {
        private List<PendingToolCall> _pendingToolCalls = new();

        public IReadOnlyList<PendingToolCall> PendingToolCalls => _pendingToolCalls;

        public void ClearPendingToolCalls() {
            _pendingToolCalls.Clear();
        }

        public void SetPendingToolCalls(List<OpenAIChatToolCall>? toolCalls) {
            _pendingToolCalls = toolCalls is { Count: > 0 }
                ? toolCalls.Select(static call => new PendingToolCall(call.Id, call.Function.Name)).ToList()
                : new List<PendingToolCall>();
        }
    }

    private sealed record PendingToolCall(string ToolCallId, string ToolName);
}
