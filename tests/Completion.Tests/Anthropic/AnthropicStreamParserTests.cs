using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Anthropic.Tests;

public sealed class AnthropicStreamParserTests {
    private static CompletionDescriptor DummyInvocation => new("test", "test-spec", "test-model");

    [Fact]
    public void ParseEvent_UsageEventsDoNotAffectBlocks() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            """
            {"type":"message_start","message":{"usage":{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":11}}}
            """,
            """
            {"type":"message_delta","delta":{"stop_reason":null},"usage":{"input_tokens":123,"output_tokens":45}}
            """,
            """
            {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":46,"cache_creation_input_tokens":7}}
            """
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        Assert.Single(result.Message.Blocks);
        Assert.IsType<ActionBlock.Text>(result.Message.Blocks[0]);
        Assert.Equal("", ((ActionBlock.Text)result.Message.Blocks[0]).Content);
    }

    [Fact]
    public void ParseEvent_AggregatesToolInputFragmentsIntoSingleToolCall() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            """
            {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_123","name":"get_weather","input":{}}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"city\":\"Par"}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"is\"}"}}
            """,
            """
            {"type":"content_block_stop","index":0}
            """
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        var toolCallBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.ToolCall);
        var toolCall = Assert.IsType<ActionBlock.ToolCall>(toolCallBlock).Call;

        Assert.Equal("toolu_123", toolCall.ToolCallId);
        Assert.Equal("get_weather", toolCall.ToolName);
        Assert.Equal("{\"city\":\"Paris\"}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public void ParseEvent_UnknownToolFallbackPreservesRawArgumentsJson() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            """
            {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_456","name":"unknown_tool","input":{}}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"flag\":\"true\",\"count\":\"42\",\"maybe\":\"null\",\"nested\":{\"enabled\":\"false\"}}"}}
            """,
            """
            {"type":"content_block_stop","index":0}
            """
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        var toolCallBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.ToolCall);
        var toolCall = Assert.IsType<ActionBlock.ToolCall>(toolCallBlock).Call;

        Assert.Equal("toolu_456", toolCall.ToolCallId);
        Assert.Equal("unknown_tool", toolCall.ToolName);
        Assert.Equal("{\"flag\":\"true\",\"count\":\"42\",\"maybe\":\"null\",\"nested\":{\"enabled\":\"false\"}}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public void ParseEvent_AggregatesThinkingDeltasIntoOpaquePayload() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            """
            {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me consider "}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"this carefully."}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc"}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"-xyz"}}
            """,
            """
            {"type":"content_block_stop","index":0}
            """
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        var thinkingBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.Thinking);
        var thinking = Assert.IsType<AnthropicReasoningBlock>(thinkingBlock);

        Assert.Equal("Let me consider this carefully.", thinking.PlainTextForDebug);

        // OpaquePayload 应当是完整的 Anthropic-native thinking content block JSON 字节
        using var doc = JsonDocument.Parse(thinking.OpaquePayload);
        Assert.Equal("thinking", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("Let me consider this carefully.", doc.RootElement.GetProperty("thinking").GetString());
        Assert.Equal("sig-abc-xyz", doc.RootElement.GetProperty("signature").GetString());
    }

    [Fact]
    public void ParseEvent_PreservesThinkingThenTextOrdering() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            """
            {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"reasoning"}}
            """,
            """
            {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}
            """,
            """
            {"type":"content_block_stop","index":0}
            """,
            """
            {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}
            """,
            """
            {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"answer"}}
            """,
            """
            {"type":"content_block_stop","index":1}
            """
        };

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        var result = aggregator.Build();

        Assert.Collection(
            result.Message.Blocks,
            block => Assert.Equal(ActionBlockKind.Thinking, block.Kind),
            block => {
                Assert.Equal(ActionBlockKind.Text, block.Kind);
                Assert.Equal("answer", ((ActionBlock.Text)block).Content);
            }
        );
    }
}
