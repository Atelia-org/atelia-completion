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

                case IActionMessage output:
                    BuildActionMessage(output, messages, state);
                    break;
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
        else if (toolResults.Results.Count > 0) {
            throw new InvalidOperationException("Tool results appeared without a preceding assistant tool_calls message.");
        }

        AppendTrailingObservation(toolResults, messages, includeExecuteError: !consumedExecuteErrorBySyntheticToolMessages);
    }

    private static Dictionary<string, ToolResult> CreateResultLookup(IReadOnlyList<ToolResult> results) {
        var lookup = new Dictionary<string, ToolResult>(StringComparer.Ordinal);

        foreach (var result in results) {
            if (string.IsNullOrWhiteSpace(result.ToolCallId)) {
                throw new InvalidOperationException("Tool result is missing tool_call_id.");
            }

            if (!lookup.TryAdd(result.ToolCallId, result)) {
                throw new InvalidOperationException($"Duplicate tool result tool_call_id='{result.ToolCallId}'.");
            }
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

    private static void BuildActionMessage(IActionMessage output, List<OpenAIChatMessage> messages, ProjectionState state) {
        EnsureNoPendingToolCalls(state, $"assistant action before tool results blockCount={output.Blocks.Count}");

        // 从 Blocks 提取：Text 块拼成 content；ToolCall 块提取为 toolCalls；Thinking 块静默跳过
        // （OpenAI Chat Completions 协议不支持这些富块类型）。
        var contentBuilder = new System.Text.StringBuilder();
        var toolCallList = new List<ParsedToolCall>(output.Blocks.Count);

        foreach (var block in output.Blocks) {
            switch (block) {
                case ActionBlock.Text textBlock:
                    contentBuilder.Append(textBlock.Content);
                    break;
                case ActionBlock.ToolCall toolCallBlock:
                    toolCallList.Add(toolCallBlock.Call);
                    break;
                case ActionBlock.Thinking:
                    // Protocol limitation: OpenAI 无 thinking 等效字段，静默跳过。
                    break;
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

        messages.Add(
            new OpenAIChatMessage {
                Role = "assistant",
                Content = content,
                ToolCalls = toolCalls
            }
        );

        state.SetPendingToolCalls(toolCalls is { Count: > 0 } ? toolCalls : null);
    }

    private static List<OpenAIChatToolCall>? BuildToolCallHistory(IReadOnlyList<ParsedToolCall> toolCalls) {
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

    private static string BuildToolCallArguments(ParsedToolCall toolCall) {
        var hasParseError = !string.IsNullOrWhiteSpace(toolCall.ParseError);

        if (!hasParseError && toolCall.Arguments is { } parsedArguments) {
            return JsonSerializer.Serialize(parsedArguments);
        }

        if (toolCall.RawArguments is { Count: > 0 } rawArguments) {
            if (hasParseError) {
                DebugUtil.Warning(
                    DebugCategory,
                    $"[OpenAI] Falling back to raw arguments toolName={toolCall.ToolName} toolCallId={toolCall.ToolCallId} error={toolCall.ParseError}"
                );
            }

            var fallback = BuildFallbackFromRawArguments(rawArguments);
            return JsonSerializer.Serialize(fallback);
        }

        if (toolCall.Arguments is { } fallbackArguments) {
            return JsonSerializer.Serialize(fallbackArguments);
        }

        if (hasParseError) {
            DebugUtil.Warning(
                DebugCategory,
                $"[OpenAI] Tool call arguments unavailable toolName={toolCall.ToolName} toolCallId={toolCall.ToolCallId} error={toolCall.ParseError}"
            );
        }

        return "{}";
    }

    private static JsonObject BuildFallbackFromRawArguments(IReadOnlyDictionary<string, string> rawArguments) {
        var node = new JsonObject();

        foreach (var pair in rawArguments) {
            node[pair.Key] = ConvertRawArgumentValue(pair.Value);
        }

        return node;
    }

    private static JsonNode? ConvertRawArgumentValue(string rawValue) {
        if (rawValue is null) { return null; }

        var trimmed = rawValue.Trim();
        if (trimmed.Length == 0) { return JsonValue.Create(rawValue); }
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)) { return null; }

        try {
            return JsonNode.Parse(trimmed);
        }
        catch (JsonException) {
        }
        catch (ArgumentException) {
        }

        return JsonValue.Create(rawValue);
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
