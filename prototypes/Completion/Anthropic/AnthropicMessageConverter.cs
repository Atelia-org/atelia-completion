using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;

namespace Atelia.Completion.Anthropic;

/// <summary>
/// 将通用的 IContextMessage 上下文转换为 Anthropic Messages API 所需的格式。
/// </summary>
internal static class AnthropicMessageConverter {
    private const string DebugCategory = "Provider";

    public static AnthropicApiRequest ConvertToApiRequest(CompletionRequest request) {
        var messages = new List<AnthropicMessage>();
        var pendingToolCallIds = new List<string>();

        foreach (var contextMessage in request.Context) {
            switch (contextMessage) {
                case ToolResultsMessage toolResults:
                    BuildToolResultsMessage(toolResults, messages, pendingToolCallIds);
                    break;

                case ObservationMessage input:
                    BuildObservationMessage(input, messages, pendingToolCallIds);
                    break;

                case IRichActionMessage richOutput:
                    BuildActionMessage(richOutput, messages, pendingToolCallIds);
                    break;

                case IActionMessage output:
                    BuildActionMessage(output, messages, pendingToolCallIds);
                    break;
            }
        }

        EnsureNoPendingToolCalls(pendingToolCallIds, "context ended");

        // Anthropic 要求第一条消息必须是 user。这一点不受后续"合并同 role 连续消息"的 normalize
        // 影响（合并不会改变首条 role），提前检查可以让职责更清晰。
        if (messages.Count > 0 && messages[0].Role != "user") {
            throw new InvalidOperationException(
                "Anthropic conversations must start with a user message; the first projected message was assistant. "
                + "Ensure the history begins with an Observation or ToolResults entry."
            );
        }

        // 确保消息序列符合 Anthropic 的交错约定
        NormalizeMessageSequence(messages);

        var apiRequest = new AnthropicApiRequest {
            Model = request.ModelId,
            MaxTokens = 4096, // 可配置
            Messages = messages,
            System = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt,
            Stream = true,
            Tools = BuildToolDefinitions(request.Tools)
        };

        DebugUtil.Info(
            DebugCategory,
            $"[Anthropic] Converted {request.Context.Count} context messages to {messages.Count} API messages, tools={apiRequest.Tools?.Count ?? 0}"
        );
        return apiRequest;
    }

    private static void BuildObservationMessage(ObservationMessage input, List<AnthropicMessage> messages, List<string> pendingToolCallIds) {
        EnsureNoPendingToolCalls(pendingToolCallIds, $"observation before tool results content={input.Content}");

        // Anthropic 拒绝空/纯空白文本块（`messages: text content blocks must contain non-whitespace text`），
        // 而"无观测"对模型也不携带任何信息，直接跳过即可，避免污染 transcript。
        if (string.IsNullOrWhiteSpace(input.Content)) { return; }

        messages.Add(
            new AnthropicMessage {
                Role = "user",
                Content = new List<AnthropicContentBlock> {
                    new AnthropicTextBlock { Text = input.Content }
                }
            }
        );
    }

    private static void BuildToolResultsMessage(
        ToolResultsMessage toolResults,
        List<AnthropicMessage> messages,
        List<string> pendingToolCallIds
    ) {
        var blocks = new List<AnthropicContentBlock>();
        var consumedExecuteErrorBySyntheticResults = false;

        if (pendingToolCallIds.Count > 0) {
            var resultsByCallId = CreateResultLookup(toolResults.Results);

            foreach (var pendingToolCallId in pendingToolCallIds) {
                if (resultsByCallId.Remove(pendingToolCallId, out var result)) {
                    blocks.Add(CreateToolResultBlock(pendingToolCallId, result.Result, result.Status != ToolExecutionStatus.Success));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(toolResults.ExecuteError)) {
                    throw new InvalidOperationException(
                        $"Tool results are missing for pending tool_use_id='{pendingToolCallId}' and no execute_error was provided."
                    );
                }

                consumedExecuteErrorBySyntheticResults = true;
                blocks.Add(CreateToolResultBlock(pendingToolCallId, toolResults.ExecuteError, isError: true));
            }

            if (resultsByCallId.Count > 0) {
                var unexpectedCallId = resultsByCallId.Keys.First();
                throw new InvalidOperationException(
                    $"Tool result tool_use_id='{unexpectedCallId}' does not match the pending assistant tool_use blocks."
                );
            }

            pendingToolCallIds.Clear();
        }
        else if (toolResults.Results.Count > 0) {
            throw new InvalidOperationException("Tool results appeared without a preceding assistant tool_use message.");
        }

        AppendTrailingObservation(
            toolResults,
            blocks,
            includeExecuteError: !consumedExecuteErrorBySyntheticResults
        );

        // 完全空（无 pending、无 results、无 content、无 error）的 ToolResultsMessage 不携带任何信息：
        // 跳过而非追加空文本块，避免触发 Anthropic 的 `text content blocks must contain non-whitespace text` 校验。
        if (blocks.Count == 0) { return; }

        messages.Add(
            new AnthropicMessage {
                Role = "user",
                Content = blocks
            }
        );
    }

    private static Dictionary<string, ToolResult> CreateResultLookup(IReadOnlyList<ToolResult> results) {
        var lookup = new Dictionary<string, ToolResult>(StringComparer.Ordinal);

        foreach (var result in results) {
            if (string.IsNullOrWhiteSpace(result.ToolCallId)) {
                throw new InvalidOperationException("Tool result is missing tool_use_id.");
            }

            if (!lookup.TryAdd(result.ToolCallId, result)) {
                throw new InvalidOperationException($"Duplicate tool result tool_use_id='{result.ToolCallId}'.");
            }
        }

        return lookup;
    }

    private static AnthropicToolResultBlock CreateToolResultBlock(string toolCallId, string content, bool isError) {
        return new AnthropicToolResultBlock {
            ToolUseId = toolCallId,
            Content = content,
            IsError = isError ? true : null // 仅在出错时写入
        };
    }

    private static void AppendTrailingObservation(
        ToolResultsMessage toolResults,
        List<AnthropicContentBlock> blocks,
        bool includeExecuteError
    ) {
        if (!string.IsNullOrWhiteSpace(toolResults.Content)) {
            blocks.Add(new AnthropicTextBlock { Text = toolResults.Content });
        }

        if (includeExecuteError && !string.IsNullOrWhiteSpace(toolResults.ExecuteError)) {
            blocks.Add(
                new AnthropicTextBlock {
                    Text = $"[Execution Error]: {toolResults.ExecuteError}"
                }
            );
        }
    }

    private static void BuildActionMessage(IActionMessage output, List<AnthropicMessage> messages, List<string> pendingToolCallIds) {
        EnsureNoPendingToolCalls(pendingToolCallIds, $"assistant action before tool results content={output.Content}");

        var blocks = new List<AnthropicContentBlock>();

        // 文本内容
        var content = output.Content;
        if (!string.IsNullOrWhiteSpace(content)) {
            blocks.Add(new AnthropicTextBlock { Text = content });
        }

        // 工具调用
        foreach (var toolCall in output.ToolCalls) {
            var toolCallId = toolCall.ToolCallId;
            var inputJson = BuildToolCallHistory(toolCall);

            blocks.Add(
                new AnthropicToolUseBlock {
                    Id = toolCallId,
                    Name = toolCall.ToolName,
                    Input = inputJson
                }
            );

            pendingToolCallIds.Add(toolCallId);
        }

        // 既无文本又无工具调用的 Action 是上游构造历史时的 bug——它在 Anthropic 协议下既无法表达
        // 也会被 API 拒绝（空 text block）。早暴露而非偷偷垫一个空块。
        if (blocks.Count == 0) {
            throw new InvalidOperationException(
                "Action message has no text content and no tool calls; nothing to send as assistant turn."
            );
        }

        messages.Add(
            new AnthropicMessage {
                Role = "assistant",
                Content = blocks
            }
        );
    }

    private static void BuildActionMessage(IRichActionMessage output, List<AnthropicMessage> messages, List<string> pendingToolCallIds) {
        EnsureNoPendingToolCalls(pendingToolCallIds, $"assistant rich action before tool results blockCount={output.Blocks.Count}");

        var blocks = new List<AnthropicContentBlock>(output.Blocks.Count);

        foreach (var block in output.Blocks) {
            switch (block) {
                case ActionBlock.Text textBlock when !string.IsNullOrWhiteSpace(textBlock.Content):
                    blocks.Add(new AnthropicTextBlock { Text = textBlock.Content });
                    break;

                case ActionBlock.ToolCall toolCallBlock:
                    blocks.Add(
                        new AnthropicToolUseBlock {
                            Id = toolCallBlock.Call.ToolCallId,
                            Name = toolCallBlock.Call.ToolName,
                            Input = BuildToolCallHistory(toolCallBlock.Call)
                        }
                    );
                    pendingToolCallIds.Add(toolCallBlock.Call.ToolCallId);
                    break;

                case ActionBlock.Thinking thinkingBlock:
                    blocks.Add(BuildThinkingBlock(thinkingBlock));
                    break;

                case ActionBlock.Text:
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported action block kind '{block.Kind}' for Anthropic projection.");
            }
        }

        if (blocks.Count == 0) {
            throw new InvalidOperationException(
                "Action message has no non-whitespace text content and no tool calls; nothing to send as assistant turn."
            );
        }

        messages.Add(
            new AnthropicMessage {
                Role = "assistant",
                Content = blocks
            }
        );
    }

    private static JsonElement BuildToolCallHistory(ParsedToolCall toolCall) {
        var hasParseError = !string.IsNullOrWhiteSpace(toolCall.ParseError);

        if (!hasParseError && toolCall.Arguments is { } parsedArguments) { return JsonSerializer.SerializeToElement(parsedArguments); }

        if (toolCall.RawArguments is { Count: > 0 } rawArguments) {
            if (hasParseError) {
                DebugUtil.Warning(
                    DebugCategory,
                    $"[Anthropic] Falling back to raw arguments toolName={toolCall.ToolName} toolCallId={toolCall.ToolCallId} error={toolCall.ParseError}"
                );
            }

            var fallback = BuildFallbackFromRawArguments(rawArguments);
            return JsonSerializer.SerializeToElement(fallback);
        }

        if (toolCall.Arguments is { } fallbackArguments) { return JsonSerializer.SerializeToElement(fallbackArguments); }

        if (hasParseError) {
            DebugUtil.Warning(
                DebugCategory,
                $"[Anthropic] Tool call arguments unavailable toolName={toolCall.ToolName} toolCallId={toolCall.ToolCallId} error={toolCall.ParseError}"
            );
        }

        return JsonSerializer.SerializeToElement(new JsonObject());
    }

    private static AnthropicThinkingBlock BuildThinkingBlock(ActionBlock.Thinking thinkingBlock) {
        return AnthropicThinkingPayloadCodec.Decode(thinkingBlock.OpaquePayload);
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
            var parsed = JsonNode.Parse(trimmed);
            return parsed;
        }
        catch (JsonException) {
            // fall back to treating the value as a plain string
        }
        catch (ArgumentException) {
            // fall back to treating the value as a plain string
        }

        return JsonValue.Create(rawValue);
    }

    /// <summary>
    /// 确保消息序列符合 Anthropic 的交错约定：user ↔ assistant。
    /// 连续的相同角色消息会被合并。
    /// </summary>
    private static void NormalizeMessageSequence(List<AnthropicMessage> messages) {
        if (messages.Count == 0) { return; }

        var normalized = new List<AnthropicMessage>();
        AnthropicMessage? pending = null;

        foreach (var message in messages) {
            if (pending is null) {
                pending = message;
                continue;
            }

            if (pending.Role == message.Role) {
                // 合并相同角色的连续消息
                pending.Content.AddRange(message.Content);
            }
            else {
                normalized.Add(pending);
                pending = message;
            }
        }

        if (pending is not null) {
            normalized.Add(pending);
        }

        messages.Clear();
        messages.AddRange(normalized);

        DebugUtil.Info(DebugCategory, $"[Anthropic] Normalized to {messages.Count} messages");
    }

    private static List<AnthropicTool>? BuildToolDefinitions(ImmutableArray<ToolDefinition> tools) {
        if (tools.IsDefaultOrEmpty) { return null; }

        var list = new List<AnthropicTool>(tools.Length);
        foreach (var definition in tools) {
            var schema = JsonToolSchemaBuilder.BuildSchema(definition);
            list.Add(
                new AnthropicTool {
                    Name = definition.Name,
                    Description = definition.Description,
                    InputSchema = schema
                }
            );
        }

        return list;
    }

    private static void EnsureNoPendingToolCalls(List<string> pendingToolCallIds, string nextContextDescription) {
        if (pendingToolCallIds.Count == 0) { return; }

        var pendingIds = string.Join(", ", pendingToolCallIds);
        throw new InvalidOperationException(
            $"Pending assistant tool_use blocks must be followed immediately by tool results before {nextContextDescription}. pending=[{pendingIds}]"
        );
    }
}
