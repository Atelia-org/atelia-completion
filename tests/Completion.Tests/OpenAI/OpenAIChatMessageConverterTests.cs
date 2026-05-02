using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIChatMessageConverterTests {
    private static CompletionDescriptor DummyInvocation => new("api.deepseek.com", "openai-chat-v1", "deepseek-v4");

    [Fact]
    public void ConvertToApiRequest_ParseErrorFallsBackToRawArguments() {
        var toolCall = new ParsedToolCall(
            ToolName: "search",
            ToolCallId: "call-1",
            RawArguments: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["message"] = "hello",
                ["count"] = "42",
                ["flag"] = "true",
                ["payload"] = "{\"nested\":1}"
            },
            Arguments: null,
            ParseError: "int32_invalid_literal",
            ParseWarning: null
        );

        var actionMessage = new ActionMessage(
            new ActionBlock[] { new ActionBlock.ToolCall(toolCall) }
        );

        var request = new CompletionRequest(
            ModelId: "gpt-4.1",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
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

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.Strict);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        Assert.NotNull(assistantMessage.ToolCalls);
        var toolCallMessage = Assert.Single(assistantMessage.ToolCalls!);
        using var arguments = JsonDocument.Parse(toolCallMessage.Function.Arguments);

        Assert.Equal("hello", arguments.RootElement.GetProperty("message").GetString());
        Assert.Equal(42, arguments.RootElement.GetProperty("count").GetInt32());
        Assert.True(arguments.RootElement.GetProperty("flag").GetBoolean());

        var payload = arguments.RootElement.GetProperty("payload");
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.Equal(1, payload.GetProperty("nested").GetInt32());
        Assert.Null(assistantMessage.Content);
    }

    [Fact]
    public void ConvertToApiRequest_ToolResultsFollowPendingAssistantToolCallOrder() {
        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.ToolCall(new ParsedToolCall("search", "call-1", new Dictionary<string, string>(), new Dictionary<string, object?>(), null, null)),
                new ActionBlock.ToolCall(new ParsedToolCall("lookup", "call-2", new Dictionary<string, string>(), new Dictionary<string, object?>(), null, null))
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
            ModelId: "gpt-4.1",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { actionMessage, toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.Strict);

        Assert.Collection(
            apiRequest.Messages,
            message => {
                Assert.Equal("assistant", message.Role);
                Assert.NotNull(message.ToolCalls);
                Assert.Equal(2, message.ToolCalls!.Count);
            },
            message => {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call-1", message.ToolCallId);
                using var document = JsonDocument.Parse(message.Content!);
                Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("ok", document.RootElement.GetProperty("result").GetString());
            },
            message => {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call-2", message.ToolCallId);
                using var document = JsonDocument.Parse(message.Content!);
                Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("bad", document.RootElement.GetProperty("result").GetString());
            },
            message => {
                Assert.Equal("user", message.Role);
                Assert.Equal("Observed external state.\n[Execution Error]: runner_failed", message.Content);
            }
        );
    }

    [Fact]
    public void ConvertToApiRequest_ExecuteErrorOnlyBackfillsPendingToolCalls() {
        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.ToolCall(new ParsedToolCall("search", "call-1", new Dictionary<string, string>(), new Dictionary<string, object?>(), null, null)),
                new ActionBlock.ToolCall(new ParsedToolCall("lookup", "call-2", new Dictionary<string, string>(), new Dictionary<string, object?>(), null, null))
            }
        );

        var toolResults = new ToolResultsMessage(
            Content: "Observed external state.",
            Results: Array.Empty<ToolResult>(),
            ExecuteError: "runner_failed"
        );

        var request = new CompletionRequest(
            ModelId: "gpt-4.1",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { actionMessage, toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.Strict);

        Assert.Collection(
            apiRequest.Messages,
            message => Assert.Equal("assistant", message.Role),
            message => {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call-1", message.ToolCallId);
                using var document = JsonDocument.Parse(message.Content!);
                Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("runner_failed", document.RootElement.GetProperty("result").GetString());
            },
            message => {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call-2", message.ToolCallId);
                using var document = JsonDocument.Parse(message.Content!);
                Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("runner_failed", document.RootElement.GetProperty("result").GetString());
            },
            message => {
                Assert.Equal("user", message.Role);
                Assert.Equal("Observed external state.", message.Content);
            }
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
            ModelId: "gpt-4.1",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.Strict)
        );

        Assert.Contains("without a preceding assistant tool_calls", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_DeepSeekDialectReplaysReasoningContentAlongsideToolCalls() {
        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                new OpenAIChatReasoningBlock("Need tool continuity.", DummyInvocation),
                new ActionBlock.ToolCall(
                    new ParsedToolCall(
                        "search",
                        "call-1",
                        new Dictionary<string, string>(),
                        new Dictionary<string, object?>(),
                        null,
                        null
                    )
                )
            }
        );

        var request = new CompletionRequest(
            ModelId: "deepseek-v4",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
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

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.DeepSeekV4);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        Assert.Equal("Need tool continuity.", assistantMessage.ReasoningContent);
        Assert.NotNull(assistantMessage.ToolCalls);
        Assert.Single(assistantMessage.ToolCalls!);
        Assert.Null(assistantMessage.Content);
    }

    [Fact]
    public void ConvertToApiRequest_StrictDialectStillIgnoresReasoningContent() {
        var request = new CompletionRequest(
            ModelId: "gpt-4.1",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ActionMessage(
                    new ActionBlock[] {
                        new OpenAIChatReasoningBlock("Should stay local.", DummyInvocation),
                        new ActionBlock.Text("hello")
                    }
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.Strict);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        Assert.Equal("hello", assistantMessage.Content);
        Assert.Null(assistantMessage.ReasoningContent);
    }
}
