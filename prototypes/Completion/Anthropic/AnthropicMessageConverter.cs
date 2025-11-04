using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

        foreach (var contextMessage in request.Context) {
            var blocks = new List<AnthropicContentBlock>();
            switch (contextMessage) {
                case ObservationMessage input:
                    if (contextMessage is ToolResultsMessage toolResults) {
                        BuildToolResultsOwnContent(toolResults, blocks);
                    }
                    BuildObservationOwnContent(input, blocks);
                    messages.Add(
                        new AnthropicMessage {
                            Role = "user",
                            Content = blocks
                        }
                    );
                    break;

                case IActionMessage output:
                    BuildActionContent(output, blocks);
                    messages.Add(
                        new AnthropicMessage {
                            Role = "assistant",
                            Content = blocks
                        }
                    );
                    break;
            }
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

        DebugUtil.Print(
            DebugCategory,
            $"[Anthropic] Converted {request.Context.Count} context messages to {messages.Count} API messages, tools={apiRequest.Tools?.Count ?? 0}"
        );
        return apiRequest;
    }

    private static void BuildToolResultsOwnContent(ToolResultsMessage toolResults, List<AnthropicContentBlock> blocks) {
        // Anthropic 要求将多个工具结果聚合到一条 user 消息中
        foreach (var result in toolResults.Results) {
            var isError = result.Status != ToolExecutionStatus.Success;

            blocks.Add(
                new AnthropicToolResultBlock {
                    ToolUseId = result.ToolCallId,
                    Content = result.Result,
                    IsError = isError ? true : null // 仅在出错时写入
                }
            );
        }

        // 如果整体执行失败，追加错误说明
        if (!string.IsNullOrEmpty(toolResults.ExecuteError)) {
            blocks.Add(
                new AnthropicTextBlock {
                    Text = $"[Execution Error]: {toolResults.ExecuteError}"
                }
            );
        }

        // 此处无需再把toolResults作为ObservationMessage重复检查Contents属性，因为每个类型派生层次仅需序列化自身声明的数据。
    }

    private static void BuildObservationOwnContent(ObservationMessage input, List<AnthropicContentBlock> blocks) {
        {
            var content = input.Contents;
            if (!string.IsNullOrWhiteSpace(content)) {
                blocks.Add(new AnthropicTextBlock { Text = content });
            }
        }

        // 确保至少有一个内容块
        if (blocks.Count == 0) {
            blocks.Add(new AnthropicTextBlock { Text = "(empty input)" });
        }
    }

    private static void BuildActionContent(IActionMessage output, List<AnthropicContentBlock> blocks) {
        // 文本内容
        var content = output.Contents;
        if (!string.IsNullOrWhiteSpace(content)) {
            blocks.Add(new AnthropicTextBlock { Text = content });
        }

        // 工具调用
        foreach (var toolCall in output.ToolCalls) {
            var inputJson = BuildToolCallHistory(toolCall);

            blocks.Add(
                new AnthropicToolUseBlock {
                    Id = toolCall.ToolCallId,
                    Name = toolCall.ToolName,
                    Input = inputJson
                }
            );
        }

        // 确保至少有一个内容块
        if (blocks.Count == 0) {
            blocks.Add(new AnthropicTextBlock { Text = string.Empty });
        }
    }

    private static JsonElement BuildToolCallHistory(ParsedToolCall toolCall) {
        var hasParseError = !string.IsNullOrWhiteSpace(toolCall.ParseError);

        if (!hasParseError && toolCall.Arguments is { } parsedArguments) { return JsonSerializer.SerializeToElement(parsedArguments); }

        if (toolCall.RawArguments is { Count: > 0 } rawArguments) {
            if (hasParseError) {
                DebugUtil.Print(
                    DebugCategory,
                    $"[Anthropic] Falling back to raw arguments toolName={toolCall.ToolName} toolCallId={toolCall.ToolCallId} error={toolCall.ParseError}"
                );
            }

            var fallback = BuildFallbackFromRawArguments(rawArguments);
            return JsonSerializer.SerializeToElement(fallback);
        }

        if (toolCall.Arguments is { } fallbackArguments) { return JsonSerializer.SerializeToElement(fallbackArguments); }

        if (hasParseError) {
            DebugUtil.Print(
                DebugCategory,
                $"[Anthropic] Tool call arguments unavailable toolName={toolCall.ToolName} toolCallId={toolCall.ToolCallId} error={toolCall.ParseError}"
            );
        }

        return JsonSerializer.SerializeToElement(new JsonObject());
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

        // Anthropic 要求第一条消息必须是 user
        if (messages.Count > 0 && messages[0].Role != "user") {
            messages.Insert(0,
                new AnthropicMessage {
                    Role = "user",
                    Content = new List<AnthropicContentBlock> {
                        new AnthropicTextBlock { Text = "(context start)" }
                }
                }
            );
        }

        DebugUtil.Print(DebugCategory, $"[Anthropic] Normalized to {messages.Count} messages");
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
}
