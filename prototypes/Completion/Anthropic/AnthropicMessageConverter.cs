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
    private const string EmptyLeadingUserPlaceholder = "<empty>";

    public static AnthropicApiRequest ConvertToApiRequest(CompletionRequest request, int? defaultMaxTokens = null) {
        var messages = new List<AnthropicMessage>();
        var pendingToolCalls = new List<PendingToolCall>();

        foreach (var contextMessage in request.Context) {
            switch (contextMessage) {
                case ToolResultsMessage toolResults:
                    BuildToolResultsMessage(toolResults, messages, pendingToolCalls);
                    break;

                case ObservationMessage input:
                    BuildObservationMessage(input, messages, pendingToolCalls);
                    break;

                case ActionMessage output:
                    BuildActionMessage(output, messages, pendingToolCalls);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported history message {DescribeHistoryMessage(contextMessage)}."
                    );
            }
        }

        EnsureNoPendingToolCalls(pendingToolCalls, "context ended");

        // 通用历史允许以 Action 开头（例如 ContextHeader 只有 Action memory carrier）。
        // Anthropic 要求第一条消息必须是 user，且拒绝空文本，因此补一个最小非空形式占位符。
        if (messages.Count > 0 && messages[0].Role != "user") {
            messages.Insert(
                0,
                new AnthropicMessage {
                    Role = "user",
                    Content = [new AnthropicTextBlock { Text = EmptyLeadingUserPlaceholder }]
                }
            );
        }

        // 确保消息序列符合 Anthropic 的交错约定
        NormalizeMessageSequence(messages);

        var apiRequest = new AnthropicApiRequest {
            Model = request.ModelId,
            MaxTokens = request.MaxTokens ?? defaultMaxTokens ?? 32000,
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

    private static void BuildObservationMessage(ObservationMessage input, List<AnthropicMessage> messages, List<PendingToolCall> pendingToolCalls) {
        EnsureNoPendingToolCalls(pendingToolCalls, $"observation before tool results content={input.Content}");

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
        List<PendingToolCall> pendingToolCalls
    ) {
        var blocks = new List<AnthropicContentBlock>();

        if (pendingToolCalls.Count > 0) {
            var resultsByCallId = CreateResultLookup(toolResults.Results);

            foreach (var pendingToolCall in pendingToolCalls) {
                if (resultsByCallId.Remove(pendingToolCall.ToolCallId, out var result)) {
                    EnsureMatchingToolName(result, pendingToolCall);
                    blocks.Add(CreateToolResultBlock(result));
                    continue;
                }

                throw new InvalidOperationException(
                    $"Tool results are missing for pending tool_use_id='{pendingToolCall.ToolCallId}'. ToolResultsMessage.Results must align 1:1 with the pending assistant tool_use blocks."
                );
            }

            if (resultsByCallId.Count > 0) {
                var unexpectedCallId = resultsByCallId.Keys.First();
                throw new InvalidOperationException(
                    $"Tool result tool_use_id='{unexpectedCallId}' does not match the pending assistant tool_use blocks."
                );
            }

            pendingToolCalls.Clear();
        }
        else if (toolResults.Results.Count > 0) { throw new InvalidOperationException("Tool results appeared without a preceding assistant tool_use message."); }

        AppendTrailingObservation(toolResults, blocks);

        // 完全空（无 pending、无 results、无 content）的 ToolResultsMessage 不携带任何信息：
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
            if (string.IsNullOrWhiteSpace(result.ToolCallId)) { throw new InvalidOperationException("Tool result is missing tool_use_id."); }

            if (!lookup.TryAdd(result.ToolCallId, result)) { throw new InvalidOperationException($"Duplicate tool result tool_use_id='{result.ToolCallId}'."); }
        }

        return lookup;
    }

    private static AnthropicToolResultBlock CreateToolResultBlock(ToolResult result) {
        var contentBlocks = new List<AnthropicToolResultContentBlock>(result.Blocks.Count);

        foreach (var block in result.Blocks) {
            switch (block) {
                case ToolResultBlock.Text textBlock:
                    contentBlocks.Add(new AnthropicToolResultTextContentBlock { Text = textBlock.Content });
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported tool result block kind '{block.Kind}' for Anthropic tool_result projection."
                    );
            }
        }

        return new AnthropicToolResultBlock {
            ToolUseId = result.ToolCallId,
            Content = contentBlocks,
            IsError = result.Status != ToolExecutionStatus.Success ? true : null // 仅在出错时写入
        };
    }

    private static void AppendTrailingObservation(ToolResultsMessage toolResults, List<AnthropicContentBlock> blocks) {
        if (!string.IsNullOrWhiteSpace(toolResults.Content)) {
            blocks.Add(new AnthropicTextBlock { Text = toolResults.Content });
        }
    }

    private static void BuildActionMessage(ActionMessage output, List<AnthropicMessage> messages, List<PendingToolCall> pendingToolCalls) {
        EnsureNoPendingToolCalls(pendingToolCalls, $"assistant action before tool results blockCount={output.Blocks.Count}");

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
                    pendingToolCalls.Add(new PendingToolCall(toolCallBlock.Call.ToolName, toolCallBlock.Call.ToolCallId));
                    break;

                case ActionBlock.ReasoningBlock reasoningBlock:
                    blocks.Add(BuildThinkingBlock(reasoningBlock));
                    break;

                case ActionBlock.Text:
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported action block kind {DescribeActionBlock(block)} for Anthropic projection.");
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

    private static string DescribeHistoryMessage(IHistoryMessage? message)
        => message is null ? "<null>" : $"type '{message.GetType().Name}' with Kind={message.Kind}";

    private static string DescribeActionBlock(ActionBlock? block)
        => block is null ? "<null>" : $"'{block.Kind}'";

    private static JsonElement BuildToolCallHistory(RawToolCall toolCall) {
        var json = StreamParserToolUtility.NormalizeRawArgumentsJson(toolCall.RawArgumentsJson);

        try {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object) { return JsonSerializer.SerializeToElement(document.RootElement); }

            DebugUtil.Warning(
                DebugCategory,
                $"[Anthropic] Tool call replay requires object input; fallback to empty object toolName={toolCall.ToolName} toolCallId={toolCall.ToolCallId} rootKind={document.RootElement.ValueKind}"
            );
        }
        catch (JsonException ex) {
            DebugUtil.Warning(
                DebugCategory,
                $"[Anthropic] Tool call replay received invalid raw arguments JSON; fallback to empty object toolName={toolCall.ToolName} toolCallId={toolCall.ToolCallId} error={ex.Message}"
            );
        }

        return JsonSerializer.SerializeToElement(new JsonObject());
    }

    private static AnthropicThinkingBlock BuildThinkingBlock(ActionBlock.ReasoningBlock reasoningBlock) {
        if (reasoningBlock is not AnthropicReasoningBlock anthropicBlock) {
            throw new InvalidOperationException(
                $"Cannot replay non-Anthropic reasoning block of type '{reasoningBlock.GetType().Name}' "
                + "via Anthropic converter. Only AnthropicReasoningBlock is supported for thinking replay."
            );
        }

        return AnthropicThinkingPayloadCodec.Decode(anthropicBlock.OpaquePayload);
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

    private static void EnsureMatchingToolName(ToolResult result, PendingToolCall pendingToolCall) {
        if (string.Equals(result.ToolName, pendingToolCall.ToolName, StringComparison.Ordinal)) { return; }

        throw new InvalidOperationException(
            $"Anthropic tool result name mismatch for tool_use_id='{pendingToolCall.ToolCallId}': expected '{pendingToolCall.ToolName}', got '{result.ToolName}'. ToolResultsMessage.Results must align by ToolCallId + ToolName."
        );
    }

    private static void EnsureNoPendingToolCalls(List<PendingToolCall> pendingToolCalls, string nextContextDescription) {
        if (pendingToolCalls.Count == 0) { return; }

        var pendingIds = string.Join(", ", pendingToolCalls.Select(static call => call.ToolCallId));
        throw new InvalidOperationException(
            $"Pending assistant tool_use blocks must be followed immediately by tool results before {nextContextDescription}. pending=[{pendingIds}]"
        );
    }

    private sealed record PendingToolCall(string ToolName, string ToolCallId);
}
