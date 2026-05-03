using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Anthropic.Tests;

public sealed class AnthropicMessageConverterTests {
    [Fact]
    public void ConvertToApiRequest_ReplaysRawArgumentsJson() {
        var toolCall = new RawToolCall(
            ToolName: "search",
            ToolCallId: "call-1",
            RawArgumentsJson: "{\"message\":\"hello\",\"count\":42,\"flag\":true,\"payload\":{\"nested\":1}}"
        );

        var actionMessage = new ActionMessage(
            new ActionBlock[] { new ActionBlock.ToolCall(toolCall) }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                actionMessage,
                new ToolResultsMessage(
                    Content: null,
                    Results: new[] {
                        new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        var toolUseBlock = Assert.IsType<AnthropicToolUseBlock>(assistantMessage.Content.Single(block => block is AnthropicToolUseBlock));
        var input = toolUseBlock.Input;

        Assert.Equal(JsonValueKind.Object, input.ValueKind);
        Assert.Equal("hello", input.GetProperty("message").GetString());
        Assert.Equal(42, input.GetProperty("count").GetInt32());
        Assert.True(input.GetProperty("flag").GetBoolean());

        var payload = input.GetProperty("payload");
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(1, payload.GetProperty("nested").GetInt32());
    }

    [Fact]
    public void ConvertToApiRequest_UsesRawArgumentsJsonObject() {
        var toolCall = new RawToolCall(
            ToolName: "echo",
            ToolCallId: "call-2",
            RawArgumentsJson: "{\"count\":7}"
        );

        var actionMessage = new ActionMessage(
            new ActionBlock[] { new ActionBlock.Text("call"), new ActionBlock.ToolCall(toolCall) }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                actionMessage,
                new ToolResultsMessage(
                    Content: null,
                    Results: new[] {
                        new ToolResult("echo", "call-2", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        var toolUseBlock = Assert.IsType<AnthropicToolUseBlock>(assistantMessage.Content.Single(block => block is AnthropicToolUseBlock));

        Assert.Equal(7, toolUseBlock.Input.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ConvertToApiRequest_UsesRawArgumentsJsonForReplay() {
        var toolCall = new RawToolCall(
            ToolName: "echo",
            ToolCallId: "call-3",
            RawArgumentsJson: "{\"count\":3}"
        );

        var actionMessage = new ActionMessage(
            new ActionBlock[] { new ActionBlock.Text("call"), new ActionBlock.ToolCall(toolCall) }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                actionMessage,
                new ToolResultsMessage(
                    Content: null,
                    Results: new[] {
                        new ToolResult("echo", "call-3", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        var toolUseBlock = Assert.IsType<AnthropicToolUseBlock>(assistantMessage.Content.Single(block => block is AnthropicToolUseBlock));

        Assert.Equal(3, toolUseBlock.Input.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ConvertToApiRequest_InvalidRawArgumentsJsonFallsBackToEmptyObject() {
        var toolCall = new RawToolCall(
            ToolName: "echo",
            ToolCallId: "call-invalid",
            RawArgumentsJson: "{\"count\":"
        );

        var actionMessage = new ActionMessage(
            new ActionBlock[] { new ActionBlock.ToolCall(toolCall) }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                actionMessage,
                new ToolResultsMessage(
                    Content: null,
                    Results: new[] {
                        new ToolResult("echo", "call-invalid", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        var toolUseBlock = Assert.IsType<AnthropicToolUseBlock>(assistantMessage.Content.Single(block => block is AnthropicToolUseBlock));

        Assert.Equal(JsonValueKind.Object, toolUseBlock.Input.ValueKind);
        Assert.Empty(toolUseBlock.Input.EnumerateObject());
    }

    [Fact]
    public void ConvertToApiRequest_NonObjectRawArgumentsJsonFallsBackToEmptyObject() {
        var toolCall = new RawToolCall(
            ToolName: "echo",
            ToolCallId: "call-array",
            RawArgumentsJson: "[1,2,3]"
        );

        var actionMessage = new ActionMessage(
            new ActionBlock[] { new ActionBlock.ToolCall(toolCall) }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                actionMessage,
                new ToolResultsMessage(
                    Content: null,
                    Results: new[] {
                        new ToolResult("echo", "call-array", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        var toolUseBlock = Assert.IsType<AnthropicToolUseBlock>(assistantMessage.Content.Single(block => block is AnthropicToolUseBlock));

        Assert.Equal(JsonValueKind.Object, toolUseBlock.Input.ValueKind);
        Assert.Empty(toolUseBlock.Input.EnumerateObject());
    }

    [Fact]
    public void ConvertToApiRequest_ToolResultsFollowPendingAssistantToolCallOrder() {
        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.ToolCall(new RawToolCall("search", "call-1", "{}")),
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-2", "{}"))
            }
        );

        var toolResults = new ToolResultsMessage(
            Content: "Observed external state.",
            Results: new[] {
                new ToolResult("lookup", "call-2", ToolExecutionStatus.Failed, "bad"),
                new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
            },
            ExecuteError: "runner_failed"
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { new ObservationMessage("hi"), actionMessage, toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        Assert.Collection(
            apiRequest.Messages,
            message => {
                Assert.Equal("user", message.Role);
                Assert.Equal("hi", Assert.IsType<AnthropicTextBlock>(Assert.Single(message.Content)).Text);
            },
            message => {
                Assert.Equal("assistant", message.Role);
                Assert.Equal(2, message.Content.Count(block => block is AnthropicToolUseBlock));
            },
            message => {
                Assert.Equal("user", message.Role);
                Assert.Collection(
                    message.Content,
                    block => {
                        var toolResult = Assert.IsType<AnthropicToolResultBlock>(block);
                        Assert.Equal("call-1", toolResult.ToolUseId);
                        Assert.Equal("ok", toolResult.Content);
                        Assert.Null(toolResult.IsError);
                    },
                    block => {
                        var toolResult = Assert.IsType<AnthropicToolResultBlock>(block);
                        Assert.Equal("call-2", toolResult.ToolUseId);
                        Assert.Equal("bad", toolResult.Content);
                        Assert.True(toolResult.IsError);
                    },
                    block => Assert.Equal("Observed external state.", Assert.IsType<AnthropicTextBlock>(block).Text),
                    block => Assert.Equal("[Execution Error]: runner_failed", Assert.IsType<AnthropicTextBlock>(block).Text)
                );
            }
        );
    }

    [Fact]
    public void ConvertToApiRequest_ExecuteErrorOnlyBackfillsPendingToolCalls() {
        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.ToolCall(new RawToolCall("search", "call-1", "{}")),
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-2", "{}"))
            }
        );

        var toolResults = new ToolResultsMessage(
            Content: "Observed external state.",
            Results: Array.Empty<ToolResult>(),
            ExecuteError: "runner_failed"
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { new ObservationMessage("hi"), actionMessage, toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var toolResultMessage = apiRequest.Messages.Last();
        Assert.Collection(
            toolResultMessage.Content,
            block => {
                var toolResult = Assert.IsType<AnthropicToolResultBlock>(block);
                Assert.Equal("call-1", toolResult.ToolUseId);
                Assert.Equal("runner_failed", toolResult.Content);
                Assert.True(toolResult.IsError);
            },
            block => {
                var toolResult = Assert.IsType<AnthropicToolResultBlock>(block);
                Assert.Equal("call-2", toolResult.ToolUseId);
                Assert.Equal("runner_failed", toolResult.Content);
                Assert.True(toolResult.IsError);
            },
            block => Assert.Equal("Observed external state.", Assert.IsType<AnthropicTextBlock>(block).Text)
        );
    }

    [Fact]
    public void ConvertToApiRequest_OrphanToolResultsThrow() {
        var toolResults = new ToolResultsMessage(
            Content: null,
            Results: new[] {
                new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
            },
            ExecuteError: null
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("without a preceding assistant tool_use", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_EmptyObservationIsSkipped() {
        // 纯空观测不携带信息，跳过可避免向 Anthropic 发送空 text block
        // (`messages: text content blocks must contain non-whitespace text`)。
        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                new ObservationMessage(null),
                new ObservationMessage("   ")
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var only = Assert.Single(apiRequest.Messages);
        Assert.Equal("user", only.Role);
        Assert.Equal("hi", Assert.IsType<AnthropicTextBlock>(Assert.Single(only.Content)).Text);
    }

    [Fact]
    public void ConvertToApiRequest_LeadingAssistantThrows() {
        // Anthropic 要求第一条消息必须是 user；上游提供了以 Action 开头的历史应被早期拒绝，
        // 而不是静默垫一个会被 API 拒绝的空文本块。
        var actionMessage = new ActionMessage(
            new ActionBlock[] { new ActionBlock.Text("hello") }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { actionMessage },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("must start with a user message", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_RichActionMessagePreservesBlockOrdering() {
        var toolCall = new RawToolCall(
            ToolName: "search",
            ToolCallId: "call-1",
            RawArgumentsJson: "{}"
        );

        var action = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.Text("alpha"),
                new ActionBlock.ToolCall(toolCall),
                new ActionBlock.Text("omega")
        }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("hi"),
                action,
                new ToolResultsMessage(
                    Content: "done",
                    Results: new[] {
                        new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);
        var assistant = apiRequest.Messages.Single(message => message.Role == "assistant");

        Assert.Collection(
            assistant.Content,
            block => Assert.Equal("alpha", Assert.IsType<AnthropicTextBlock>(block).Text),
            block => Assert.Equal("call-1", Assert.IsType<AnthropicToolUseBlock>(block).Id),
            block => Assert.Equal("omega", Assert.IsType<AnthropicTextBlock>(block).Text)
        );
    }

    [Fact]
    public void ConvertToApiRequest_RichActionMessageRoundTripsThinkingPayload() {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new {
                type = "thinking",
                thinking = "Let me reason about the tool result.",
                signature = "sig-123"
            }
        );

        var action = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.Text("alpha"),
                new AnthropicReasoningBlock(payload, new CompletionDescriptor("provider", "spec", "model"), "debug"),
                new ActionBlock.Text("omega")
        }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { new ObservationMessage("hi"), action },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);
        var assistant = apiRequest.Messages.Single(message => message.Role == "assistant");

        Assert.Collection(
            assistant.Content,
            block => Assert.Equal("alpha", Assert.IsType<AnthropicTextBlock>(block).Text),
            block => {
                var thinking = Assert.IsType<AnthropicThinkingBlock>(block);
                Assert.Equal("Let me reason about the tool result.", thinking.Thinking);
                Assert.Equal("sig-123", thinking.Signature);
            },
            block => Assert.Equal("omega", Assert.IsType<AnthropicTextBlock>(block).Text)
        );
    }

    [Fact]
    public void ConvertToApiRequest_InvalidThinkingPayloadFailsFast() {
        var action = new ActionMessage(
            new ActionBlock[] {
                new AnthropicReasoningBlock(
                    System.Text.Encoding.UTF8.GetBytes("""{"type":"not-thinking","foo":1}"""),
                    new CompletionDescriptor("provider", "spec", "model"),
                    null
                )
        }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { new ObservationMessage("hi"), action },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("Failed to deserialize Anthropic thinking block payload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_NonAnthropicReasoningBlockFailsFast() {
        var action = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.TextReasoningBlock(
                    "plain reasoning",
                    new CompletionDescriptor("provider", "spec", "model")
                )
        }
        );

        var request = new CompletionRequest(
            ModelId: "claude-3",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { new ObservationMessage("hi"), action },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("Cannot replay non-Anthropic reasoning block", exception.Message, StringComparison.Ordinal);
    }
}
