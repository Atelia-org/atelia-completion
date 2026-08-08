using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIChatMessageConverterTests {
    private static CompletionDescriptor DummyInvocation => new("api.deepseek.com", "openai-chat-v1", "deepseek-v4");

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
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                actionMessage,
                new ToolResultsMessage(
                    content: null,
                    results: new[] {
                        ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "ok")
                    }
                )
            }
            ),
            tailMessages: []
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
                new ActionBlock.ToolCall(new RawToolCall("search", "call-1", "{}")),
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-2", "{}"))
            }
        );

        var toolResults = new ToolResultsMessage(
            content: "Observed external state.",
            results: new[] {
                ToolResult.FromText("lookup", "call-2", ToolExecutionStatus.Failed, "bad"),
                new ToolResult(
                    "search",
                    "call-1",
                    ToolExecutionStatus.Success,
                    new ToolResultBlock[] {
                        new ToolResultBlock.Text("alpha"),
                        new ToolResultBlock.Text("omega")
                    }
                )
            }
        );

        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { actionMessage, toolResults }
            ),
            tailMessages: []
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
                Assert.Equal("alphaomega", document.RootElement.GetProperty("result").GetString());
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
                Assert.Equal("Observed external state.", message.Content);
            }
        );
    }

    [Fact]
    public void ConvertToApiRequest_ToolDependencyCanCrossPrefixTailBoundary() {
        var action = new ActionMessage([
            new ActionBlock.ToolCall(
                new RawToolCall("search", "call-1", "{}")
            )
        ]);
        var results = new ToolResultsMessage(
            content: null,
            results: [
                ToolResult.FromText(
                    "search",
                    "call-1",
                    ToolExecutionStatus.Success,
                    "ok"
                )
            ]
        );
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault([]),
                [action]
            ),
            [results]
        );

        OpenAIChatApiRequest apiRequest =
            OpenAIChatMessageConverter.ConvertToApiRequest(
                request,
                OpenAIChatDialects.Strict
            );

        Assert.Contains(
            apiRequest.Messages,
            static message => message.ToolCalls is { Count: > 0 }
        );
        Assert.Contains(
            apiRequest.Messages,
            static message => message.ToolCallId == "call-1"
        );
    }

    [Fact]
    public void ConvertToApiRequest_MapsRequiredNamedAndParallelPolicy() {
        var tool = new ToolDefinition(
            "emit_result",
            "Emit one result.",
            new ToolSchema.Object()
        );
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                new CompletionOutputContract(
                    [tool],
                    CompletionToolChoice.RequiredNamed("emit_result"),
                    allowParallelToolCalls: false
                ),
                [new ObservationMessage("emit")]
            ),
            tailMessages: []
        );

        OpenAIChatApiRequest apiRequest =
            OpenAIChatMessageConverter.ConvertToApiRequest(
                request,
                OpenAIChatDialects.Strict
            );

        var choice = Assert.IsType<OpenAIChatNamedToolChoice>(
            apiRequest.ToolChoice
        );
        Assert.Equal("function", choice.Type);
        Assert.Equal("emit_result", choice.Function.Name);
        Assert.False(apiRequest.ParallelToolCalls);
    }

    [Fact]
    public void ConvertToApiRequest_MissingPendingToolResultsThrow() {
        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.ToolCall(new RawToolCall("search", "call-1", "{}")),
                new ActionBlock.ToolCall(new RawToolCall("lookup", "call-2", "{}"))
            }
        );

        var toolResults = new ToolResultsMessage(
            content: "Observed external state.",
            results: new[] {
                ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "ok")
            }
        );

        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { actionMessage, toolResults }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.Strict)
        );

        Assert.Contains("call-2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("align 1:1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_ToolNameMismatchThrows() {
        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.ToolCall(new RawToolCall("search", "call-1", "{}"))
            }
        );

        var toolResults = new ToolResultsMessage(
            content: null,
            results: new[] {
                ToolResult.FromText("lookup", "call-1", ToolExecutionStatus.Success, "ok")
            }
        );

        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { actionMessage, toolResults }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.Strict)
        );

        Assert.Contains("expected 'search'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("got 'lookup'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ToolCallId + ToolName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_OrphanToolResultsThrow() {
        var toolResults = new ToolResultsMessage(
            content: null,
            results: new[] {
                ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "ok")
            }
        );

        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { toolResults }
            ),
            tailMessages: []
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
                    new RawToolCall(
                        "search",
                        "call-1",
                        "{}"
                    )
                )
            }
        );

        var request = new CompletionRequest(
            "deepseek-v4",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                actionMessage,
                new ToolResultsMessage(
                    content: null,
                    results: new[] {
                        ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "ok")
                    }
                )
            }
            ),
            tailMessages: []
        );

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.DeepSeekV4);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        Assert.Equal("Need tool continuity.", assistantMessage.ReasoningContent);
        Assert.NotNull(assistantMessage.ToolCalls);
        Assert.Single(assistantMessage.ToolCalls!);
        Assert.Null(assistantMessage.Content);
    }

    [Fact]
    public void ConvertToApiRequest_DeepSeekReasoningOriginMustMatchTargetInvocation() {
        var source = new CompletionDescriptor(
            DummyInvocation.ProviderId,
            DummyInvocation.ApiSpecId,
            "deepseek-other"
        );
        var request = new CompletionRequest(
            DummyInvocation.Model,
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                [new ActionMessage([
                new OpenAIChatReasoningBlock("reason", source)
            ])]
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIChatMessageConverter.ConvertToApiRequest(
                request,
                OpenAIChatDialects.DeepSeekV4,
                DummyInvocation
            )
        );

        Assert.Contains("requires Origin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_DeepSeekRejectsGenericTextReasoningReplay() {
        var request = new CompletionRequest(
            DummyInvocation.Model,
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                [new ActionMessage([
                new ActionBlock.TextReasoningBlock("reason", DummyInvocation)
            ])]
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIChatMessageConverter.ConvertToApiRequest(
                request,
                OpenAIChatDialects.DeepSeekV4,
                DummyInvocation
            )
        );

        Assert.Contains(nameof(OpenAIChatReasoningBlock), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_StrictDialectStillIgnoresReasoningContent() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    new ActionBlock[] {
                        new OpenAIChatReasoningBlock("Should stay local.", DummyInvocation),
                        new ActionBlock.Text("hello")
                    }
                )
            }
            ),
            tailMessages: []
        );

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(request, OpenAIChatDialects.Strict);

        var assistantMessage = apiRequest.Messages.Single(message => message.Role == "assistant");
        Assert.Equal("hello", assistantMessage.Content);
        Assert.Null(assistantMessage.ReasoningContent);
    }
}
