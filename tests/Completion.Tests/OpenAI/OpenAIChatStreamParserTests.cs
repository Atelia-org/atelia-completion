using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIChatStreamParserTests {
    private static CompletionDescriptor DummyInvocation => new("test", "test-spec", "test-model");

    [Fact]
    public void ParseEvent_IgnoresReasoningContentAndAggregatesToolCallFragments() {
        var parser = new OpenAIChatStreamParser(
            OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls
        );
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\",\"reasoning_content\":null,\"tool_calls\":null},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":\"The model is thinking\",\"tool_calls\":null},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":[{\"id\":\"call_123\",\"index\":0,\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"\"}}]},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":[{\"id\":null,\"index\":0,\"type\":\"function\",\"function\":{\"name\":null,\"arguments\":\"{\\\"city\\\": \\\"\"}}]},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":[{\"id\":null,\"index\":0,\"type\":\"function\",\"function\":{\"name\":null,\"arguments\":\"Paris\\\"}\"}}]},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":\"\\n\",\"reasoning_content\":null,\"tool_calls\":null},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":null},\"finish_reason\":\"tool_calls\"}],\"usage\":null}"
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        Assert.DoesNotContain(result.Message.Blocks, b => b.Kind == ActionBlockKind.Text && ((ActionBlock.Text)b).Content.Length > 0);

        var toolCallBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.ToolCall);
        var toolCall = Assert.IsType<ActionBlock.ToolCall>(toolCallBlock).Call;
        Assert.Equal("call_123", toolCall.ToolCallId);
        Assert.Equal("get_weather", toolCall.ToolName);
        Assert.Equal("{\"city\": \"Paris\"}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public void ParseEvent_DeepSeekModeCapturesReasoningContentBeforeToolCalls() {
        var parser = new OpenAIChatStreamParser(
            OpenAIChatWhitespaceContentMode.Preserve,
            OpenAIChatReasoningMode.ReplayCompatible
        );
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":\"The model is \",\"tool_calls\":null},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":\"thinking\",\"tool_calls\":null},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":[{\"id\":\"call_123\",\"index\":0,\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Paris\\\"}\"}}]},\"finish_reason\":null}],\"usage\":null}",
            "{\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"default\",\"choices\":[{\"index\":0,\"delta\":{\"role\":null,\"content\":null,\"reasoning_content\":null,\"tool_calls\":null},\"finish_reason\":\"tool_calls\"}],\"usage\":null}"
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        var reasoningBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.Thinking);
        Assert.Equal(
            "The model is thinking",
            Assert.IsType<OpenAIChatReasoningBlock>(reasoningBlock).Content
        );

        var toolCallBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.ToolCall);
        var toolCall = Assert.IsType<ActionBlock.ToolCall>(toolCallBlock).Call;
        Assert.Equal("call_123", toolCall.ToolCallId);
        Assert.Equal("get_weather", toolCall.ToolName);
        Assert.Equal("{\"city\":\"Paris\"}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public void ParseEvent_DeepSeekUsageMapsHitAndMissAsReadAndUncached() {
        var parser = CreateDeepSeekUsageParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        ParseTerminalEvent(parser, aggregator);
        parser.ParseEvent(
            """
            {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_cache_hit_tokens":80,"prompt_cache_miss_tokens":20}}
            """,
            aggregator
        );

        CompletionUsage usage = aggregator.Build().Usage;
        Assert.Equal(20, usage.UncachedInputTokens);
        Assert.Null(usage.CacheCreationInputTokens);
        Assert.Equal(80, usage.CacheReadInputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(
            PromptCacheObservationStatus.Partial,
            usage.PromptCache.ObservationStatus
        );
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 100, 0)]
    [InlineData(long.MaxValue, long.MaxValue - 1, 1)]
    public void ParseEvent_DeepSeekUsageAcceptsExactPromptPartition(
        long promptTokens,
        long cacheHitTokens,
        long cacheMissTokens
    ) {
        var parser = CreateDeepSeekUsageParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        ParseTerminalEvent(parser, aggregator);
        parser.ParseEvent(
            $"{{\"choices\":[],\"usage\":{{\"prompt_tokens\":{promptTokens},\"prompt_cache_hit_tokens\":{cacheHitTokens},\"prompt_cache_miss_tokens\":{cacheMissTokens}}}}}",
            aggregator
        );

        CompletionUsage usage = aggregator.Build().Usage;
        Assert.Equal(cacheMissTokens, usage.UncachedInputTokens);
        Assert.Equal(cacheHitTokens, usage.CacheReadInputTokens);
        Assert.Null(usage.CacheCreationInputTokens);
        Assert.Equal(
            PromptCacheObservationStatus.Partial,
            usage.PromptCache.ObservationStatus
        );
        Assert.False(usage.IsNoCacheIoObserved);
    }

    [Fact]
    public void ParseEvent_DeepSeekUsageWithoutCacheFieldsDoesNotInventUncachedTokens() {
        var parser = CreateDeepSeekUsageParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        ParseTerminalEvent(parser, aggregator);
        parser.ParseEvent(
            """
            {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5}}
            """,
            aggregator
        );

        CompletionUsage usage = aggregator.Build().Usage;
        Assert.Null(usage.UncachedInputTokens);
        Assert.Null(usage.CacheCreationInputTokens);
        Assert.Null(usage.CacheReadInputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(
            PromptCacheObservationStatus.Unavailable,
            usage.PromptCache.ObservationStatus
        );
    }

    [Theory]
    [InlineData("{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":80}")]
    [InlineData("{\"prompt_tokens\":100,\"prompt_cache_miss_tokens\":20}")]
    [InlineData("{\"prompt_cache_hit_tokens\":80,\"prompt_cache_miss_tokens\":20}")]
    [InlineData("{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":null,\"prompt_cache_miss_tokens\":20}")]
    [InlineData("{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":80,\"prompt_cache_miss_tokens\":null}")]
    [InlineData("{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":-1,\"prompt_cache_miss_tokens\":101}")]
    [InlineData("{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":80.5,\"prompt_cache_miss_tokens\":19.5}")]
    [InlineData("{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":80,\"prompt_cache_miss_tokens\":19}")]
    [InlineData("{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":80,\"prompt_cache_miss_tokens\":21}")]
    [InlineData("{\"prompt_tokens\":9223372036854775807,\"prompt_cache_hit_tokens\":9223372036854775807,\"prompt_cache_miss_tokens\":1}")]
    public void ParseEvent_DeepSeekUsageRejectsPartialMalformedOrMismatchedCacheFields(
        string usageJson
    ) {
        var parser = CreateDeepSeekUsageParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        ParseTerminalEvent(parser, aggregator);

        Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                $"{{\"choices\":[],\"usage\":{usageJson}}}",
                aggregator
            )
        );
    }

    [Fact]
    public void ParseEvent_DeepSeekCumulativeUsageSnapshotsReplaceRatherThanAccumulate() {
        var parser = CreateDeepSeekUsageParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        ParseTerminalEvent(parser, aggregator);
        parser.ParseEvent(
            """
            {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":1,"prompt_cache_hit_tokens":8,"prompt_cache_miss_tokens":2}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_cache_hit_tokens":80,"prompt_cache_miss_tokens":20}}
            """,
            aggregator
        );

        CompletionUsage usage = aggregator.Build().Usage;
        Assert.Equal(20, usage.UncachedInputTokens);
        Assert.Equal(80, usage.CacheReadInputTokens);
        Assert.Equal(5, usage.OutputTokens);
    }

    [Fact]
    public void ParseEvent_OpenAIUsageShapeDoesNotInterpretDeepSeekCacheFields() {
        var parser = new OpenAIChatStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        ParseTerminalEvent(parser, aggregator);
        parser.ParseEvent(
            """
            {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_cache_hit_tokens":80,"prompt_cache_miss_tokens":20}}
            """,
            aggregator
        );

        CompletionUsage usage = aggregator.Build().Usage;
        Assert.Null(usage.UncachedInputTokens);
        Assert.Null(usage.CacheReadInputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(
            PromptCacheObservationStatus.Unavailable,
            usage.PromptCache.ObservationStatus
        );
    }

    [Fact]
    public void ParseEvent_StrictModePreservesWhitespaceContentDuringToolCalls() {
        var parser = new OpenAIChatStreamParser(
            OpenAIChatWhitespaceContentMode.Preserve
        );
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"id\":\"call_123\",\"index\":0,\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Paris\\\"}\"}}]},\"finish_reason\":null}],\"usage\":null}",
            "{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\\n\"},\"finish_reason\":null}],\"usage\":null}",
            "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":null}"
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        var contentBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.Text && ((ActionBlock.Text)b).Content.Length > 0);
        Assert.Equal("\n", ((ActionBlock.Text)contentBlock).Content);

        var toolCallBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.ToolCall);
        Assert.Equal("call_123", Assert.IsType<ActionBlock.ToolCall>(toolCallBlock).Call.ToolCallId);
    }

    [Fact]
    public void ParseEvent_SgLangModeIgnoresWhitespaceWhenContentAndToolCallsShareSameDelta() {
        var parser = new OpenAIChatStreamParser(
            OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls
        );
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            """
            {"choices":[{"index":0,"delta":{"content":"\n","tool_calls":[{"id":"call_123","index":0,"type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}]},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":null}
            """
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        Assert.DoesNotContain(result.Message.Blocks, b => b.Kind == ActionBlockKind.Text && ((ActionBlock.Text)b).Content.Length > 0);

        var toolCallBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.ToolCall);
        Assert.Equal("call_123", Assert.IsType<ActionBlock.ToolCall>(toolCallBlock).Call.ToolCallId);
        Assert.Equal("{\"city\":\"Paris\"}", Assert.IsType<ActionBlock.ToolCall>(toolCallBlock).Call.RawArgumentsJson);
    }

    [Fact]
    public void ParseEvent_InterleavedToolCallsAreAggregatedByIndex() {
        var parser = new OpenAIChatStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            """
            {"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_a","index":0,"type":"function","function":{"name":"alpha","arguments":"{\"value\": \"A\"}"}},{"id":"call_b","index":1,"type":"function","function":{"name":"beta","arguments":"{\"count\": "}}]},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"tool_calls":[{"id":null,"index":1,"type":"function","function":{"name":null,"arguments":"7}"}}]},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":null}
            """
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        var toolCallBlocks = result.Message.Blocks.Where(b => b.Kind == ActionBlockKind.ToolCall).ToArray();
        Assert.Equal(2, toolCallBlocks.Length);
        Assert.Equal("alpha", Assert.IsType<ActionBlock.ToolCall>(toolCallBlocks[0]).Call.ToolName);
        Assert.Equal("{\"value\": \"A\"}", Assert.IsType<ActionBlock.ToolCall>(toolCallBlocks[0]).Call.RawArgumentsJson);
        Assert.Equal("beta", Assert.IsType<ActionBlock.ToolCall>(toolCallBlocks[1]).Call.ToolName);
        Assert.Equal("{\"count\": 7}", Assert.IsType<ActionBlock.ToolCall>(toolCallBlocks[1]).Call.RawArgumentsJson);
        Assert.Equal("call_b", Assert.IsType<ActionBlock.ToolCall>(toolCallBlocks[1]).Call.ToolCallId);
    }

    [Theory]
    [InlineData("stop", CompletionTerminationKind.Completed)]
    [InlineData("tool_calls", CompletionTerminationKind.Completed)]
    [InlineData("function_call", CompletionTerminationKind.Completed)]
    [InlineData("length", CompletionTerminationKind.Incomplete)]
    [InlineData("content_filter", CompletionTerminationKind.Incomplete)]
    public void ParseEvent_FinishReasonIsSemanticTerminal(
        string finishReason,
        CompletionTerminationKind expectedKind
    ) {
        var parser = new OpenAIChatStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            $$"""
            {"choices":[{"index":0,"delta":{},"finish_reason":"{{finishReason}}"}]}
            """,
            aggregator
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(expectedKind, result.Termination.Kind);
        Assert.Equal(finishReason, result.Termination.ProviderReason);
    }

    [Fact]
    public void ParseEvent_TopLevelErrorIsFailedSemanticTerminal() {
        const string providerErrorMessage =
            "provider rejected stream: endpoint=https://provider.invalid secret=raw-detail";
        var parser = new OpenAIChatStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"error":{"message":"provider rejected stream: endpoint=https://provider.invalid secret=raw-detail"}}
            """,
            aggregator
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal([providerErrorMessage], result.Errors);
    }

    [Fact]
    public void ProviderErrorDiagnostic_IsFixedAndContentFree() {
        var diagnostic = OpenAIChatStreamParser.CreateProviderErrorDiagnostic();

        Assert.Equal("Provider", diagnostic.Category);
        Assert.Equal("[OpenAI] Provider error received.", diagnostic.Text);
        Assert.Equal(Atelia.Diagnostics.DebugEventKind.Failure, diagnostic.EventKind);
        Assert.DoesNotContain("provider.invalid", diagnostic.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-detail", diagnostic.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"choices\":{}}")]
    [InlineData("{\"choices\":[{\"delta\":7,\"finish_reason\":null}]}")]
    public void ParseEvent_MalformedProviderShapeThrowsProtocolException(string json) {
        var parser = new OpenAIChatStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        Assert.Throws<InvalidDataException>(() => parser.ParseEvent(json, aggregator));
        Assert.False(parser.TerminalEventObserved);
    }

    [Fact]
    public void ParseEvent_RejectsMultipleChoices() {
        var parser = new OpenAIChatStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """
                {"choices":[{"index":0,"delta":{},"finish_reason":"stop"},{"index":1,"delta":{},"finish_reason":"stop"}]}
                """,
                aggregator
            )
        );

        Assert.Contains("n=1", exception.Message, StringComparison.Ordinal);
        Assert.False(parser.TerminalEventObserved);
    }

    [Fact]
    public void ParseEvent_RejectsNonDefaultChoiceIndex() {
        var parser = new OpenAIChatStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """{"choices":[{"index":1,"delta":{},"finish_reason":"stop"}]}""",
                aggregator
            )
        );

        Assert.Contains("choice index 0", exception.Message, StringComparison.Ordinal);
        Assert.False(parser.TerminalEventObserved);
    }

    [Fact]
    public void ParseEvent_ObserverInvalidOperationExceptionPreservesIdentity() {
        var parser = new OpenAIChatStreamParser(
            reasoningMode: OpenAIChatReasoningMode.ReplayCompatible
        );
        var observer = new CompletionStreamObserver();
        var expected = new InvalidOperationException("scripted observer failure");
        observer.ReceivedThinkingBegin += () => throw expected;
        var aggregator = new CompletionAggregator(DummyInvocation, observer);

        var actual = Assert.Throws<InvalidOperationException>(
            () => parser.ParseEvent(
                """
                {"choices":[{"index":0,"delta":{"reasoning_content":"thinking"},"finish_reason":null}]}
                """,
                aggregator
            )
        );

        Assert.Same(expected, actual);
    }

    private static OpenAIChatStreamParser CreateDeepSeekUsageParser() => new(
        usageShape: OpenAIChatUsageShape.DeepSeekPromptCacheHitMiss
    );

    private static void ParseTerminalEvent(
        OpenAIChatStreamParser parser,
        CompletionAggregator aggregator
    ) => parser.ParseEvent(
        """
        {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}
        """,
        aggregator
    );
}
