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
}
