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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ObservationMessage("hi"),
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ObservationMessage("hi"),
                actionMessage,
                new ToolResultsMessage(
                    content: null,
                    results: new[] {
                        ToolResult.FromText("echo", "call-2", ToolExecutionStatus.Success, "ok")
                    }
                )
            }
            ),
            tailMessages: []
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ObservationMessage("hi"),
                actionMessage,
                new ToolResultsMessage(
                    content: null,
                    results: new[] {
                        ToolResult.FromText("echo", "call-3", ToolExecutionStatus.Success, "ok")
                    }
                )
            }
            ),
            tailMessages: []
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ObservationMessage("hi"),
                actionMessage,
                new ToolResultsMessage(
                    content: null,
                    results: new[] {
                        ToolResult.FromText("echo", "call-invalid", ToolExecutionStatus.Success, "ok")
                    }
                )
            }
            ),
            tailMessages: []
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ObservationMessage("hi"),
                actionMessage,
                new ToolResultsMessage(
                    content: null,
                    results: new[] {
                        ToolResult.FromText("echo", "call-array", ToolExecutionStatus.Success, "ok")
                    }
                )
            }
            ),
            tailMessages: []
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi"), actionMessage, toolResults }
            ),
            tailMessages: []
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
                        Assert.Collection(
                            toolResult.Content,
                            contentBlock => Assert.Equal("alpha", Assert.IsType<AnthropicToolResultTextContentBlock>(contentBlock).Text),
                            contentBlock => Assert.Equal("omega", Assert.IsType<AnthropicToolResultTextContentBlock>(contentBlock).Text)
                        );
                        Assert.Null(toolResult.IsError);
                    },
                    block => {
                        var toolResult = Assert.IsType<AnthropicToolResultBlock>(block);
                        Assert.Equal("call-2", toolResult.ToolUseId);
                        var contentBlock = Assert.Single(toolResult.Content);
                        Assert.Equal("bad", Assert.IsType<AnthropicToolResultTextContentBlock>(contentBlock).Text);
                        Assert.True(toolResult.IsError);
                    },
                    block => Assert.Equal("Observed external state.", Assert.IsType<AnthropicTextBlock>(block).Text)
                );
            }
        );

        var json = JsonSerializer.Serialize(apiRequest);
        using var document = JsonDocument.Parse(json);
        var toolResultContent = document.RootElement
            .GetProperty("messages")[2]
            .GetProperty("content")[0]
            .GetProperty("content")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(2, toolResultContent.Length);
        Assert.Equal("text", toolResultContent[0].GetProperty("type").GetString());
        Assert.Equal("alpha", toolResultContent[0].GetProperty("text").GetString());
        Assert.Equal("text", toolResultContent[1].GetProperty("type").GetString());
        Assert.Equal("omega", toolResultContent[1].GetProperty("text").GetString());
    }

    [Fact]
    public void ConvertToApiRequest_MissingPendingToolResultsThrows() {
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi"), actionMessage, toolResults }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi"), actionMessage, toolResults }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { toolResults }
            ),
            tailMessages: []
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ObservationMessage("hi"),
                new ObservationMessage(null),
                new ObservationMessage("   ")
            }
            ),
            tailMessages: []
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var only = Assert.Single(apiRequest.Messages);
        Assert.Equal("user", only.Role);
        Assert.Equal("hi", Assert.IsType<AnthropicTextBlock>(Assert.Single(only.Content)).Text);
    }

    [Fact]
    public void ConvertToApiRequest_LeadingAssistantGetsNonEmptyUserPlaceholder() {
        // 通用历史允许 Action 开头；Anthropic projection 用非空占位 user 满足首消息约束。
        var actionMessage = new ActionMessage(
            new ActionBlock[] { new ActionBlock.Text("hello") }
        );

        var request = new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ObservationMessage(null),
                actionMessage,
                new ObservationMessage("next")
            }
            ),
            tailMessages: []
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        Assert.Collection(
            apiRequest.Messages,
            message => {
                Assert.Equal("user", message.Role);
                Assert.Equal("<empty>", Assert.IsType<AnthropicTextBlock>(Assert.Single(message.Content)).Text);
            },
            message => {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("hello", Assert.IsType<AnthropicTextBlock>(Assert.Single(message.Content)).Text);
            },
            message => {
                Assert.Equal("user", message.Role);
                Assert.Equal("next", Assert.IsType<AnthropicTextBlock>(Assert.Single(message.Content)).Text);
            }
        );
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ObservationMessage("hi"),
                action,
                new ToolResultsMessage(
                    content: "done",
                    results: new[] {
                        ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "ok")
                    }
                )
            }
            ),
            tailMessages: []
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
                new AnthropicReasoningBlock(
                    payload,
                    new CompletionDescriptor("provider", "spec", "model"),
                    "Let me reason about the tool result."
                ),
                new ActionBlock.Text("omega")
        }
        );

        var request = new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi"), action }
            ),
            tailMessages: []
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi"), action }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("Failed to deserialize Anthropic thinking block payload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_ThinkingOriginMustMatchTargetInvocation() {
        var payload = AnthropicThinkingPayloadCodec.Encode("reason", "sig");
        var source = new CompletionDescriptor("old-host", "anthropic-messages-v1", "claude-old");
        var target = new CompletionDescriptor("new-host", "anthropic-messages-v1", "claude-new");
        var request = new CompletionRequest(
            target.Model,
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                [
                new ObservationMessage("hi"),
                new ActionMessage([
                    new AnthropicReasoningBlock(payload, source, "reason")
                ])
            ]
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(
                request,
                targetInvocation: target
            )
        );

        Assert.Contains("requires Origin", exception.Message, StringComparison.Ordinal);
        Assert.Contains("old-host", exception.Message, StringComparison.Ordinal);
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi"), action }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("Cannot replay non-Anthropic reasoning block", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_PromptCachingDisabledByDefault_LeavesSystemStringAndNoBreakpoints() {
        var request = BuildToolLoopRequest();

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        Assert.IsType<string>(apiRequest.System);
        Assert.Null(Assert.Single(apiRequest.Tools!).CacheControl);
        Assert.All(
            apiRequest.Messages.SelectMany(message => message.Content),
            block => Assert.Null(block.CacheControl)
        );
    }

    [Fact]
    public void ConvertToApiRequest_PromptCachingEnabled_MarksToolsSystemAndLastMessageBreakpoints() {
        var request = BuildToolLoopRequest();

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request, defaultMaxTokens: null, enablePromptCaching: true);

        // system 段：转为 content-block 数组并在其上打断点。
        var systemBlocks = Assert.IsType<List<AnthropicSystemTextBlock>>(apiRequest.System);
        var systemBlock = Assert.Single(systemBlocks);
        Assert.Equal("You are a helpful assistant.", systemBlock.Text);
        Assert.Equal("ephemeral", Assert.IsType<AnthropicCacheControl>(systemBlock.CacheControl).Type);

        // tools 段：最后一个 tool 打断点。
        Assert.Equal("ephemeral", Assert.IsType<AnthropicCacheControl>(Assert.Single(apiRequest.Tools!).CacheControl).Type);

        // messages 段：仅最后一条消息的最后一个内容块打断点。
        var lastMessage = apiRequest.Messages[^1];
        Assert.NotNull(lastMessage.Content[^1].CacheControl);
        var markedBlocks = apiRequest.Messages
            .SelectMany(message => message.Content)
            .Count(block => block.CacheControl is not null);
        Assert.Equal(1, markedBlocks);
    }

    [Fact]
    public void ConvertToApiRequest_PromptCachingEnabled_SerializesSystemArrayWithCacheControl() {
        var request = BuildToolLoopRequest();

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request, defaultMaxTokens: null, enablePromptCaching: true);

        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(apiRequest, options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var systemArray = root.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, systemArray.ValueKind);
        var firstSystem = systemArray[0];
        Assert.Equal("text", firstSystem.GetProperty("type").GetString());
        Assert.Equal("ephemeral", firstSystem.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.False(
            firstSystem.GetProperty("cache_control").TryGetProperty("ttl", out _)
        );

        var toolsArray = root.GetProperty("tools");
        var lastTool = toolsArray[toolsArray.GetArrayLength() - 1];
        Assert.Equal("ephemeral", lastTool.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.False(
            lastTool.GetProperty("cache_control").TryGetProperty("ttl", out _)
        );
        var messages = root.GetProperty("messages");
        var lastMessage = messages[messages.GetArrayLength() - 1];
        var content = lastMessage.GetProperty("content");
        Assert.False(
            content[content.GetArrayLength() - 1]
                .GetProperty("cache_control")
                .TryGetProperty("ttl", out _)
        );
    }

    [Theory]
    [InlineData(AnthropicPromptCacheTtl.FiveMinutes, "5m")]
    [InlineData(AnthropicPromptCacheTtl.OneHour, "1h")]
    public void ConvertToApiRequest_PromptCacheTtlSerializesOnAllBreakpoints(
        AnthropicPromptCacheTtl promptCacheTtl,
        string expectedWireTtl
    ) {
        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(
            BuildToolLoopRequest(),
            enablePromptCaching: true,
            promptCacheTtl: promptCacheTtl
        );
        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(apiRequest, options)
        );
        JsonElement root = document.RootElement;
        JsonElement systemCacheControl = root
            .GetProperty("system")[0]
            .GetProperty("cache_control");
        JsonElement tools = root.GetProperty("tools");
        JsonElement toolCacheControl = tools[tools.GetArrayLength() - 1]
            .GetProperty("cache_control");
        JsonElement messages = root.GetProperty("messages");
        JsonElement lastMessage = messages[messages.GetArrayLength() - 1];
        JsonElement content = lastMessage.GetProperty("content");
        JsonElement messageCacheControl = content[content.GetArrayLength() - 1]
            .GetProperty("cache_control");

        Assert.Equal(expectedWireTtl, systemCacheControl.GetProperty("ttl").GetString());
        Assert.Equal(expectedWireTtl, toolCacheControl.GetProperty("ttl").GetString());
        Assert.Equal(expectedWireTtl, messageCacheControl.GetProperty("ttl").GetString());
    }

    [Fact]
    public void ConvertToApiRequest_PromptCachingEnabledWithoutSystem_SkipsSystemBreakpoint() {
        var request = new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi") }
            ),
            tailMessages: []
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request, defaultMaxTokens: null, enablePromptCaching: true);

        Assert.Null(apiRequest.System);
        Assert.Null(apiRequest.Tools);
        var lastMessage = apiRequest.Messages[^1];
        Assert.NotNull(lastMessage.Content[^1].CacheControl);
    }

    [Fact]
    public void ConvertToApiRequest_SameRoleMergeKeepsCacheMarkerAtPrefixBoundary() {
        var request = new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault([]),
                [new ObservationMessage("shared-prefix")]
            ),
            [new ObservationMessage("member-tail")]
        );

        AnthropicApiRequest apiRequest =
            AnthropicMessageConverter.ConvertToApiRequest(
                request,
                enablePromptCaching: true
            );

        AnthropicMessage message = Assert.Single(apiRequest.Messages);
        Assert.Equal("user", message.Role);
        Assert.Collection(
            message.Content,
            block => {
                Assert.Equal(
                    "shared-prefix",
                    Assert.IsType<AnthropicTextBlock>(block).Text
                );
                Assert.NotNull(block.CacheControl);
            },
            block => {
                Assert.Equal(
                    "member-tail",
                    Assert.IsType<AnthropicTextBlock>(block).Text
                );
                Assert.Null(block.CacheControl);
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
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault([]),
                [action]
            ),
            [results]
        );

        AnthropicApiRequest apiRequest =
            AnthropicMessageConverter.ConvertToApiRequest(request);

        Assert.Contains(
            apiRequest.Messages.SelectMany(static message => message.Content),
            static block => block is AnthropicToolUseBlock
        );
        Assert.Contains(
            apiRequest.Messages.SelectMany(static message => message.Content),
            static block => block is AnthropicToolResultBlock
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
            "claude-3",
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

        AnthropicApiRequest apiRequest =
            AnthropicMessageConverter.ConvertToApiRequest(request);

        Assert.Equal("tool", apiRequest.ToolChoice!.Type);
        Assert.Equal("emit_result", apiRequest.ToolChoice.Name);
        Assert.True(apiRequest.ToolChoice.DisableParallelToolUse);
    }

    [Fact]
    public void ConvertToApiRequest_ForcedToolChoiceRejectsExtendedThinking() {
        var tool = new ToolDefinition(
            "emit_result",
            "Emit one result.",
            new ToolSchema.Object()
        );
        var request = new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                new CompletionOutputContract(
                    [tool],
                    CompletionToolChoice.RequiredAny
                ),
                [new ObservationMessage("emit")]
            ),
            tailMessages: []
        );

        Assert.Throws<NotSupportedException>(
            () => AnthropicMessageConverter.ConvertToApiRequest(
                request,
                reasoningEffort: CompletionReasoningEffort.High
            )
        );
    }

    [Fact]
    public void ConvertToApiRequest_ReasoningDisabledSendsExplicitDisabledThinking() {
        var request = new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi") }
            ),
            tailMessages: [],
            maxTokens: 8000
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(
            request,
            reasoningEffort: CompletionReasoningEffort.Disabled
        );

        Assert.NotNull(apiRequest.Thinking);
        Assert.Equal("disabled", apiRequest.Thinking.Type);
        Assert.Null(apiRequest.Thinking.Display);
        Assert.Null(apiRequest.OutputConfig);
        Assert.Null(apiRequest.Temperature);
        Assert.Null(apiRequest.TopP);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Low, "low")]
    [InlineData(CompletionReasoningEffort.Medium, "medium")]
    [InlineData(CompletionReasoningEffort.High, "high")]
    [InlineData(CompletionReasoningEffort.Max, "max")]
    public void ConvertToApiRequest_ReasoningEffortUsesAdaptiveThinking(
        CompletionReasoningEffort reasoningEffort,
        string expectedEffort
    ) {
        var request = new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi") }
            ),
            tailMessages: []
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(
            request,
            reasoningEffort: reasoningEffort
        );

        Assert.Equal("adaptive", Assert.IsType<AnthropicThinkingConfig>(apiRequest.Thinking).Type);
        Assert.Equal("summarized", apiRequest.Thinking.Display);
        Assert.Equal(expectedEffort, Assert.IsType<AnthropicOutputConfig>(apiRequest.OutputConfig).Effort);
    }

    [Fact]
    public void ConvertToApiRequest_ProviderDefaultOmitsReasoningControls() {
        var request = new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi") }
            ),
            tailMessages: []
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        Assert.Null(apiRequest.Thinking);
        Assert.Null(apiRequest.OutputConfig);
    }

    [Fact]
    public void ConvertToApiRequest_ReplaysRedactedThinkingBlock() {
        var payload = AnthropicThinkingPayloadCodec.EncodeRedacted("EmwKAhgBEgy3va3p");

        var action = new ActionMessage(
            new ActionBlock[] {
                new AnthropicReasoningBlock(payload, new CompletionDescriptor("provider", "spec", "model")),
                new ActionBlock.Text("omega")
            }
        );

        var request = new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hi"), action }
            ),
            tailMessages: []
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);
        var assistant = apiRequest.Messages.Single(message => message.Role == "assistant");

        Assert.Collection(
            assistant.Content,
            block => Assert.Equal("EmwKAhgBEgy3va3p", Assert.IsType<AnthropicRedactedThinkingBlock>(block).Data),
            block => Assert.Equal("omega", Assert.IsType<AnthropicTextBlock>(block).Text)
        );
    }

    private static CompletionRequest BuildToolLoopRequest() {
        var action = new ActionMessage(
            new ActionBlock[] { new ActionBlock.ToolCall(new RawToolCall("search", "call-1", "{}")) }
        );

        return new CompletionRequest(
            "claude-3",
            new CompletionPromptPrefix(
                "You are a helpful assistant.",
                CompletionOutputContract.ProviderDefault(ImmutableArray.Create(CreateSimpleTool())),
                new IHistoryMessage[] {
                new ObservationMessage("Please search."),
                action,
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
    }

    private static ToolDefinition CreateSimpleTool()
        => new(
            name: "search",
            description: "Search for something.",
            inputSchema: new ToolSchema.Object(
                properties: [
                    new ToolSchema.Property(
                        "query",
                        new ToolSchema.Value(ToolParamType.String, description: "Query text."),
                        isRequired: true
                    )
                ]
            )
        );
}
