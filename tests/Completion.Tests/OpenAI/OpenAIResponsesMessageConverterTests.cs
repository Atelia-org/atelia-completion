using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesMessageConverterTests {
    [Fact]
    public void ConvertToApiRequest_ProjectsObservationActionAndToolResultsIntoResponsesInput() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                "Follow the house style.",
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ObservationMessage("Search the docs."),
                new ObservationMessage("   "),
                new ActionMessage(
                    new ActionBlock[] {
                        new ActionBlock.Text("I'll check"),
                        new ActionBlock.Text(" now."),
                        new ActionBlock.ToolCall(new RawToolCall("search_docs", "call-1", """{"query":"atelia"}""")),
                        new ActionBlock.Text("Waiting for tool output.")
                    }
                ),
                new ToolResultsMessage(
                    content: "Tool finished; continue.",
                    results: [
                        new ToolResult(
                            "search_docs",
                            "call-1",
                            ToolExecutionStatus.Success,
                            [
                                new ToolResultBlock.Text("Found "),
                                new ToolResultBlock.Text("3 matches.")
                            ]
                        )
                    ]
                )
            }
            ),
            tailMessages: []
        );

        var apiRequest = OpenAIResponsesMessageConverter.ConvertToApiRequest(request);

        Assert.Equal("gpt-4.1", apiRequest.Model);
        Assert.Equal("Follow the house style.", apiRequest.Instructions);
        Assert.True(apiRequest.Stream);
        Assert.False(apiRequest.Store);
        Assert.True(apiRequest.ParallelToolCalls);
        Assert.Equal(["reasoning.encrypted_content"], apiRequest.Include);

        Assert.Collection(
            apiRequest.Input,
            item => AssertUserMessage(item, "Search the docs."),
            item => AssertAssistantMessage(item, "I'll check now."),
            item => {
                var functionCall = Assert.IsType<OpenAIResponsesFunctionCallItem>(item);
                Assert.Equal("call-1", functionCall.CallId);
                Assert.Equal("search_docs", functionCall.Name);
                Assert.Equal("""{"query":"atelia"}""", functionCall.Arguments);
            },
            item => AssertAssistantMessage(item, "Waiting for tool output."),
            item => {
                var functionOutput = Assert.IsType<OpenAIResponsesFunctionCallOutputItem>(item);
                Assert.Equal("call-1", functionOutput.CallId);
                Assert.Equal("Found 3 matches.", functionOutput.Output);
            },
            item => AssertUserMessage(item, "Tool finished; continue.")
        );
    }

    [Theory]
    [InlineData("recap_grid.control")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ConvertToApiRequest_RejectsProviderIncompatibleHistoricalToolCallName(
        string toolName
    ) {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault([]),
                [
                    new ActionMessage([
                        new ActionBlock.ToolCall(
                            new RawToolCall(toolName, "call-1", "{}")
                        )
                    ]),
                    new ToolResultsMessage(
                        content: null,
                        results: [
                            ToolResult.FromText(
                                toolName,
                                "call-1",
                                ToolExecutionStatus.Success,
                                "ok"
                            )
                        ]
                    )
                ]
            ),
            tailMessages: []
        );

        CompletionRequestRejectedException exception = Assert.Throws<
            CompletionRequestRejectedException
        >(() => OpenAIResponsesMessageConverter.ConvertToApiRequest(request));

        Assert.Equal(
            "openai.responses.invalid-function-name",
            exception.Termination.ProviderReason
        );
        Assert.Contains(
            "1-64 ASCII letters",
            exception.Termination.Detail ?? string.Empty,
            StringComparison.Ordinal
        );
        Assert.Equal(["adapter-validation=function-name"], exception.Errors);
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

        OpenAIResponsesApiRequest apiRequest =
            OpenAIResponsesMessageConverter.ConvertToApiRequest(request);

        Assert.Contains(
            apiRequest.Input,
            static item => item is OpenAIResponsesFunctionCallItem
        );
        Assert.Contains(
            apiRequest.Input,
            static item => item is OpenAIResponsesFunctionCallOutputItem
        );
    }

    [Fact]
    public void ConvertToApiRequest_MapsNativeRequiredNamedAndParallelPolicy() {
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

        OpenAIResponsesApiRequest apiRequest =
            OpenAIResponsesMessageConverter.ConvertToApiRequest(request);

        var choice = Assert.IsType<OpenAIResponsesNamedToolChoice>(
            apiRequest.ToolChoice
        );
        Assert.Equal("function", choice.Type);
        Assert.Equal("emit_result", choice.Name);
        Assert.False(apiRequest.ParallelToolCalls);
    }

    [Fact]
    public void ConvertToApiRequest_LowersSingletonRequiredNamedWhenNativeShapeIsUnsupported() {
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

        OpenAIResponsesApiRequest apiRequest =
            OpenAIResponsesMessageConverter.ConvertToApiRequest(
                request,
                supportsNativeRequiredNamedToolChoice: false
            );

        Assert.Equal("required", apiRequest.ToolChoice);
        Assert.Equal("emit_result", Assert.Single(apiRequest.Tools!).Name);
        Assert.False(apiRequest.ParallelToolCalls);
    }

    [Fact]
    public void ConvertToApiRequest_RejectsMultiToolRequiredNamedWhenNativeShapeIsUnsupported() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                new CompletionOutputContract(
                    [
                        new ToolDefinition(
                            "emit_result",
                            "Emit one result.",
                            new ToolSchema.Object()
                        ),
                        new ToolDefinition(
                            "lookup",
                            "Look up context.",
                            new ToolSchema.Object()
                        )
                    ],
                    CompletionToolChoice.RequiredNamed("emit_result")
                ),
                [new ObservationMessage("emit")]
            ),
            tailMessages: []
        );

        NotSupportedException exception = Assert.Throws<
            NotSupportedException
        >(() => OpenAIResponsesMessageConverter.ConvertToApiRequest(
            request,
            supportsNativeRequiredNamedToolChoice: false
        ));

        Assert.Contains("exactly the named tool", exception.Message);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, "none", null)]
    [InlineData(CompletionReasoningEffort.Low, "low", "auto")]
    [InlineData(CompletionReasoningEffort.Medium, "medium", "auto")]
    [InlineData(CompletionReasoningEffort.High, "high", "auto")]
    [InlineData(CompletionReasoningEffort.Max, "xhigh", "auto")]
    public void ConvertToApiRequest_MapsReasoningEffortAndRequestsReadableSummary(
        CompletionReasoningEffort effort,
        string expectedEffort,
        string? expectedSummary
    ) {
        var request = new CompletionRequest(
            "gpt-5",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                [new ObservationMessage("hi")]
            ),
            tailMessages: []
        );

        var apiRequest = OpenAIResponsesMessageConverter.ConvertToApiRequest(
            request,
            new OpenAIResponsesClientOptions { ReasoningEffort = effort }
        );

        var reasoning = Assert.IsType<OpenAIResponsesReasoningConfig>(apiRequest.Reasoning);
        Assert.Equal(expectedEffort, reasoning.Effort);
        Assert.Equal(expectedSummary, reasoning.Summary);
    }

    [Fact]
    public void ConvertToApiRequest_ProviderDefaultOmitsReasoningControl() {
        var request = new CompletionRequest(
            "gpt-5",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                [new ObservationMessage("hi")]
            ),
            tailMessages: []
        );

        Assert.Null(OpenAIResponsesMessageConverter.ConvertToApiRequest(request).Reasoning);
    }

    [Fact]
    public void ConvertToApiRequest_MissingPendingToolResultsThrow() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    [
                        new ActionBlock.ToolCall(new RawToolCall("search", "call-1", "{}")),
                        new ActionBlock.ToolCall(new RawToolCall("lookup", "call-2", "{}"))
                    ]
                ),
                new ToolResultsMessage(
                    content: null,
                    results: [
                        ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "ok")
                    ]
                )
            }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIResponsesMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("call-2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("align 1:1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_ToolNameMismatchThrows() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    [
                        new ActionBlock.ToolCall(new RawToolCall("search", "call-1", "{}"))
                    ]
                ),
                new ToolResultsMessage(
                    content: null,
                    results: [
                        ToolResult.FromText("lookup", "call-1", ToolExecutionStatus.Success, "ok")
                    ]
                )
            }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIResponsesMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("expected 'search'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("got 'lookup'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ToolCallId + ToolName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_OrphanToolResultsThrow() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                [
                new ToolResultsMessage(
                    content: null,
                    results: [
                        ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "ok")
                    ]
                )
            ]
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIResponsesMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("without a preceding function_call", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_ReplaysOpenAIResponsesReasoningBlockAsReasoningInputItem() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    [
                        new OpenAIResponsesReasoningBlock(
                            """{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"Need tool."}],"encrypted_content":"enc_123"}""",
                            new CompletionDescriptor("openai", "openai-responses-v2", "gpt-4.1"),
                            "Need tool."
                        )
                    ]
                )
            }
            ),
            tailMessages: []
        );

        var apiRequest = OpenAIResponsesMessageConverter.ConvertToApiRequest(request);

        var reasoningItem = Assert.IsType<OpenAIResponsesReasoningItem>(Assert.Single(apiRequest.Input));
        Assert.NotNull(reasoningItem.ExtensionData);
        Assert.Equal("rs_1", reasoningItem.ExtensionData!["id"].GetString());
        Assert.Equal("enc_123", reasoningItem.ExtensionData["encrypted_content"].GetString());
    }

    [Fact]
    public void ConvertToApiRequest_ForeignReasoningReplayFailsFast() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    [
                        new ActionBlock.TextReasoningBlock(
                            "Need tool.",
                            new CompletionDescriptor("anthropic", "anthropic-messages-v1", "claude")
                        )
                    ]
                )
            }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIResponsesMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains(nameof(OpenAIResponsesReasoningBlock), exception.Message, StringComparison.Ordinal);
        Assert.Contains("Cross-provider reasoning replay is not supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_OpaqueReasoningCarrierDoesNotAuthorizeReplay() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    [
                        new ActionBlock.OpaqueReasoningBlock(
                            "atelia.openai-responses.reasoning-item-json.v1",
                            System.Text.Encoding.UTF8.GetBytes(
                                """{"type":"reasoning","id":"rs_1","encrypted_content":"enc_123"}"""
                            ),
                            new CompletionDescriptor(
                                "openai",
                                "openai-responses-v2",
                                "gpt-4.1"
                            )
                        )
                    ]
                )
            }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIResponsesMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains(nameof(OpenAIResponsesReasoningBlock), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ActionBlock.OpaqueReasoningBlock), exception.Message, StringComparison.Ordinal);
        Assert.Contains("Cross-provider reasoning replay is not supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_OpenAIResponsesReasoningWithMismatchedOriginFailsFast() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    [
                        new OpenAIResponsesReasoningBlock(
                            """{"type":"reasoning","id":"rs_1","encrypted_content":"enc_123"}""",
                            new CompletionDescriptor("openai", "openai-chat-v1", "gpt-4.1")
                        )
                    ]
                )
            }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIResponsesMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("Origin.ApiSpecId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("openai-responses-v2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_ReasoningOriginMustMatchFullTargetInvocation() {
        var source = new CompletionDescriptor(
            "old-host",
            "openai-responses-v2",
            "gpt-5"
        );
        var target = new CompletionDescriptor(
            "new-host",
            "openai-responses-v2",
            "gpt-5"
        );
        var request = new CompletionRequest(
            target.Model,
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                [new ActionMessage([
                new OpenAIResponsesReasoningBlock(
                    """{"type":"reasoning","id":"rs_1","encrypted_content":"enc_123"}""",
                    source
                )
            ])]
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIResponsesMessageConverter.ConvertToApiRequest(
                request,
                targetInvocation: target
            )
        );

        Assert.Contains("requires Origin", exception.Message, StringComparison.Ordinal);
        Assert.Contains("old-host", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_RejectsPlainTextThatDivergesFromReasoningItem() {
        var origin = new CompletionDescriptor(
            "openai",
            "openai-responses-v2",
            "gpt-5"
        );
        var request = new CompletionRequest(
            origin.Model,
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                [new ActionMessage([
                new OpenAIResponsesReasoningBlock(
                    """{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"authoritative"}]}""",
                    origin,
                    "forged"
                )
            ])]
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAIResponsesMessageConverter.ConvertToApiRequest(request)
        );

        Assert.Contains("PlainText", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertUserMessage(OpenAIResponsesInputItem item, string expectedText) {
        var message = Assert.IsType<OpenAIResponsesMessageItem>(item);
        Assert.Equal("user", message.Role);
        var content = Assert.Single(message.Content);
        Assert.Equal(expectedText, Assert.IsType<OpenAIResponsesInputTextContentItem>(content).Text);
    }

    private static void AssertAssistantMessage(OpenAIResponsesInputItem item, string expectedText) {
        var message = Assert.IsType<OpenAIResponsesMessageItem>(item);
        Assert.Equal("assistant", message.Role);
        var content = Assert.Single(message.Content);
        Assert.Equal(expectedText, Assert.IsType<OpenAIResponsesOutputTextContentItem>(content).Text);
    }
}
