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
            ParseNamedEvent(parser, e, aggregator);
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
            {"type":"message_start","message":{}}
            """,
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
            ParseNamedEvent(parser, e, aggregator);
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
            {"type":"message_start","message":{}}
            """,
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
            ParseNamedEvent(parser, e, aggregator);
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
            {"type":"message_start","message":{}}
            """,
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
            ParseNamedEvent(parser, e, aggregator);
        }

        var result = aggregator.Build();

        var thinkingBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.Thinking);
        var thinking = Assert.IsType<AnthropicReasoningBlock>(thinkingBlock);

        Assert.Equal("Let me consider this carefully.", thinking.PlainText);

        // OpaquePayload 应当是完整的 Anthropic-native thinking content block JSON 字节
        using var doc = JsonDocument.Parse(thinking.OpaquePayload);
        Assert.Equal("thinking", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("Let me consider this carefully.", doc.RootElement.GetProperty("thinking").GetString());
        Assert.Equal("sig-abc-xyz", doc.RootElement.GetProperty("signature").GetString());
    }

    [Fact]
    public void ParseEvent_ThinkingWithoutSignatureFailsAtBlockBoundary() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        ParseNamedEvent(
            parser,
            """{"type":"message_start","message":{}}""",
            aggregator
        );
        ParseNamedEvent(
            parser,
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
            aggregator
        );
        ParseNamedEvent(
            parser,
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"reason"}}""",
            aggregator
        );

        var exception = Assert.Throws<InvalidDataException>(
            () => ParseNamedEvent(
                parser,
                """{"type":"content_block_stop","index":0}""",
                aggregator
            )
        );

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseEvent_PreservesRedactedThinkingForReplay() {
        var parser = new AnthropicStreamParser();
        var observer = new CompletionStreamObserver();
        var thinkingBeginCount = 0;
        observer.ReceivedThinkingBegin += () => thinkingBeginCount++;
        var aggregator = new CompletionAggregator(DummyInvocation, observer);

        var events = new[] {
            """
            {"type":"message_start","message":{}}
            """,
            """
            {"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"EmwKAhgBEgy3va3p"}}
            """,
            """
            {"type":"content_block_stop","index":0}
            """
        };

        foreach (var e in events) {
            ParseNamedEvent(parser, e, aggregator);
        }

        var result = aggregator.Build();

        var thinkingBlock = Assert.Single(result.Message.Blocks, b => b.Kind == ActionBlockKind.Thinking);
        var thinking = Assert.IsType<AnthropicReasoningBlock>(thinkingBlock);

        Assert.Null(thinking.PlainText);
        Assert.Equal(1, thinkingBeginCount);

        using var doc = JsonDocument.Parse(thinking.OpaquePayload);
        Assert.Equal("redacted_thinking", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("EmwKAhgBEgy3va3p", doc.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public void ParseEvent_PreservesThinkingThenTextOrdering() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var events = new[] {
            """
            {"type":"message_start","message":{}}
            """,
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
            ParseNamedEvent(parser, e, aggregator);
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

    [Theory]
    [InlineData("end_turn", CompletionTerminationKind.Completed)]
    [InlineData("tool_use", CompletionTerminationKind.Completed)]
    [InlineData("max_tokens", CompletionTerminationKind.Incomplete)]
    [InlineData("future_stop_reason", CompletionTerminationKind.Incomplete)]
    public void ParseEvent_MessageStopIsExplicitTerminalAndPreservesStopReasonMapping(
        string stopReason,
        CompletionTerminationKind expectedKind
    ) {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"message_start","message":{}}""",
            aggregator,
            "message_start"
        );
        parser.ParseEvent(
            """{"type":"message_delta","delta":{"stop_reason":"STOP_REASON"}}"""
                .Replace("STOP_REASON", stopReason, StringComparison.Ordinal),
            aggregator,
            "message_delta"
        );
        parser.ParseEvent(
            """{"type":"message_stop"}""",
            aggregator,
            "message_stop"
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(expectedKind, result.Termination.Kind);
        Assert.Equal(stopReason, result.Termination.ProviderReason);
    }

    [Theory]
    [InlineData("end_turn", CompletionTerminationKind.Completed)]
    [InlineData("tool_use", CompletionTerminationKind.Completed)]
    [InlineData("max_tokens", CompletionTerminationKind.Incomplete)]
    [InlineData("future_stop_reason", CompletionTerminationKind.Incomplete)]
    public void TryFinalizeAtCleanEndOfStream_UsesAuthoritativeStopReason(
        string stopReason,
        CompletionTerminationKind expectedKind
    ) {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"message_start","message":{}}""",
            aggregator,
            "message_start"
        );
        parser.ParseEvent(
            """{"type":"message_delta","delta":{"stop_reason":"STOP_REASON"}}"""
                .Replace("STOP_REASON", stopReason, StringComparison.Ordinal),
            aggregator,
            "message_delta"
        );

        Assert.True(parser.TryFinalizeAtCleanEndOfStream(aggregator));
        Assert.False(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(expectedKind, result.Termination.Kind);
        Assert.Equal(stopReason, result.Termination.ProviderReason);
    }

    [Fact]
    public void TryFinalizeAtCleanEndOfStream_RejectsActiveContentBlock() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"message_start","message":{}}""",
            aggregator,
            "message_start"
        );
        parser.ParseEvent(
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            aggregator,
            "content_block_start"
        );

        Assert.False(parser.TryFinalizeAtCleanEndOfStream(aggregator));
        Assert.Contains(
            "activeBlockIndexes=0",
            parser.DescribeInterruptionState(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ParseEvent_MessageStopWithoutAuthoritativeStopReasonIsProtocolError() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"message_start","message":{}}""",
            aggregator,
            "message_start"
        );

        Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """{"type":"message_stop"}""",
                aggregator,
                "message_stop"
            )
        );
        Assert.False(parser.TerminalEventObserved);
    }

    [Fact]
    public void ParseEvent_ErrorIsTerminalAndPreservesProviderErrorType() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}
            """,
            aggregator,
            "error"
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal("overloaded_error", result.Termination.ProviderReason);
        Assert.Equal("Overloaded", result.Termination.Detail);
        Assert.Equal(["Overloaded"], result.Errors);
    }

    [Fact]
    public void ParseEvent_PingAndMatchingUnknownEventAreNonTerminal() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent("""{"type":"ping"}""", aggregator, "ping");
        parser.ParseEvent(
            """{"type":"future_progress","value":42}""",
            aggregator,
            "future_progress"
        );

        Assert.False(parser.TerminalEventObserved);
    }

    [Theory]
    [InlineData("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}")]
    [InlineData("message_stop", "{\"type\":\"message_stop\"}")]
    [InlineData("content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}")]
    [InlineData("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"x\"}}")]
    [InlineData("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}")]
    public void ParseEvent_KnownMessageLifecycleEventBeforeStartIsProtocolError(
        string eventType,
        string json
    ) {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(json, aggregator, eventType)
        );

        Assert.Contains("before message_start", exception.Message, StringComparison.Ordinal);
        Assert.False(parser.TerminalEventObserved);
    }

    [Fact]
    public void ParseEvent_RepeatedMessageStartIsProtocolError() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);
        const string MessageStart = """{"type":"message_start","message":{}}""";

        parser.ParseEvent(MessageStart, aggregator, "message_start");

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(MessageStart, aggregator, "message_start")
        );
        Assert.Contains("repeated message_start", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_MessageDeltaBeforeActiveBlockStopIsProtocolError() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"message_start","message":{}}""",
            aggregator,
            "message_start"
        );
        parser.ParseEvent(
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            aggregator,
            "content_block_start"
        );

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
                aggregator,
                "message_delta"
            )
        );
        Assert.Contains("active content block", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_RejectsNonSequentialOrOverlappingContentBlockStarts() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"message_start","message":{}}""",
            aggregator,
            "message_start"
        );

        var nonSequential = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
                aggregator,
                "content_block_start"
            )
        );
        Assert.Contains("expected index 0", nonSequential.Message, StringComparison.Ordinal);

        parser.ParseEvent(
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            aggregator,
            "content_block_start"
        );
        var overlapping = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
                aggregator,
                "content_block_start"
            )
        );
        Assert.Contains("active content block", overlapping.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_ContentBlockAfterMessageDeltaIsProtocolError() {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"message_start","message":{}}""",
            aggregator,
            "message_start"
        );
        parser.ParseEvent(
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
            aggregator,
            "message_delta"
        );

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                aggregator,
                "content_block_start"
            )
        );
        Assert.Contains("after message_delta", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "{\"type\":\"ping\"}")]
    [InlineData("message_start", "{\"type\":\"ping\"}")]
    [InlineData("message_start", "{")]
    [InlineData("message_start", "[]")]
    [InlineData("message_start", "{\"type\":\"message_start\"}")]
    [InlineData("content_block_start", "{\"type\":\"content_block_start\",\"content_block\":{\"type\":\"text\"}}")]
    [InlineData("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0}")]
    [InlineData("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}")]
    [InlineData("message_delta", "{\"type\":\"message_delta\",\"delta\":7}")]
    [InlineData("error", "{\"type\":\"error\",\"error\":{\"message\":\"bad\"}}")]
    public void ParseEvent_RejectsUnnamedMismatchedOrMalformedKnownEvents(
        string? sseEventType,
        string json
    ) {
        var parser = new AnthropicStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(json, aggregator, sseEventType)
        );
        Assert.False(parser.TerminalEventObserved);
    }

    private static void ParseNamedEvent(
        AnthropicStreamParser parser,
        string json,
        CompletionAggregator aggregator
    ) {
        using var document = JsonDocument.Parse(json);
        var eventType = document.RootElement.GetProperty("type").GetString();
        parser.ParseEvent(json, aggregator, eventType);
    }
}
