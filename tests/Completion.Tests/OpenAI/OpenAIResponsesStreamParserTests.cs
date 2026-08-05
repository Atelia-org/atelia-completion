using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesStreamParserTests {
    private static CompletionDescriptor DummyInvocation => new("openai", "openai-responses-v1", "gpt-5");

    [Fact]
    public void ParseEvent_AggregatesReasoningToolCallAndTextFromMinimalEventSet() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"Need tool."}],"encrypted_content":"abc"}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"city\":\"Par"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"city\":\"Paris\"}","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather"}}
            """,
            aggregator
        );
        parser.ParseEvent("""{"type":"response.output_text.delta","delta":"Sunny."}""", aggregator);
        parser.ParseEvent("""{"type":"response.completed"}""", aggregator);

        var result = aggregator.Build();

        Assert.Collection(
            result.Message.Blocks,
            block => {
                var reasoning = Assert.IsType<OpenAIResponsesReasoningBlock>(block);
                Assert.Equal("Need tool.", reasoning.PlainTextForDebug);
                Assert.Contains("\"encrypted_content\":\"abc\"", reasoning.RawItemJson, StringComparison.Ordinal);
            },
            block => {
                var toolCall = Assert.IsType<ActionBlock.ToolCall>(block).Call;
                Assert.Equal("call_123", toolCall.ToolCallId);
                Assert.Equal("get_weather", toolCall.ToolName);
                Assert.Equal("{\"city\":\"Paris\"}", toolCall.RawArgumentsJson);
            },
            block => Assert.Equal("Sunny.", Assert.IsType<ActionBlock.Text>(block).Content)
        );
    }

    [Fact]
    public void ParseEvent_OutputItemDoneFinalizesFunctionCallWithoutArgumentsDone() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"city\":\"Paris\"}"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}
            """,
            aggregator
        );

        var result = aggregator.Build();

        var toolCall = Assert.IsType<ActionBlock.ToolCall>(Assert.Single(result.Message.Blocks)).Call;
        Assert.Equal("call_123", toolCall.ToolCallId);
        Assert.Equal("get_weather", toolCall.ToolName);
        Assert.Equal("{\"city\":\"Paris\"}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public void ParseEvent_OutputItemDoneDoesNotDuplicateFunctionCallAfterArgumentsDone() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"city\":\"Par"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"city\":\"Paris\"}","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather"}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}
            """,
            aggregator
        );

        var result = aggregator.Build();

        var toolCall = Assert.IsType<ActionBlock.ToolCall>(Assert.Single(result.Message.Blocks)).Call;
        Assert.Equal("call_123", toolCall.ToolCallId);
        Assert.Equal("get_weather", toolCall.ToolName);
        Assert.Equal("{\"city\":\"Paris\"}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public void ParseEvent_FailedIsSemanticTerminalAndAppendsError() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.failed","response":{"error":{"message":"stream failed"}}}
            """,
            aggregator
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal(["stream failed"], result.Errors);
    }

    [Fact]
    public void ParseEvent_TopLevelErrorIsSemanticTerminalAndAppendsError() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"error":{"message":"bad input"}}
            """,
            aggregator
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal(["bad input"], result.Errors);
    }

    [Fact]
    public void ParseEvent_ResponseIncompletePreservesProviderReason() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.output_item.added","item":{"id":"rs_pending","type":"reasoning"}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.delta","item_id":"fc_pending","delta":"{\"city\":"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.incomplete","response":{"incomplete_details":{"reason":"max_output_tokens"}}}
            """,
            aggregator,
            "response.incomplete"
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Incomplete, result.Termination.Kind);
        Assert.Equal("max_output_tokens", result.Termination.ProviderReason);
        Assert.Contains("max_output_tokens", result.Termination.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_UnknownWellFormedEventIsForwardCompatible() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"response.future_progress","future_value":42}""",
            aggregator,
            "response.future_progress"
        );

        Assert.False(parser.TerminalEventObserved);
        parser.ParseEvent(
            """{"type":"response.completed"}""",
            aggregator,
            "response.completed"
        );

        Assert.True(parser.TerminalEventObserved);
        Assert.Equal(CompletionTerminationKind.Completed, aggregator.Build().Termination.Kind);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"type\":7}")]
    public void ParseEvent_MalformedProviderShapeThrowsProtocolException(string json) {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        Assert.Throws<InvalidDataException>(() => parser.ParseEvent(json, aggregator));
        Assert.False(parser.TerminalEventObserved);
    }

    [Fact]
    public void ParseEvent_RejectsSseEventAndDataTypeMismatch() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """{"type":"response.completed"}""",
                aggregator,
                "response.output_text.delta"
            )
        );

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.False(parser.TerminalEventObserved);
    }

    [Theory]
    [InlineData("{\"type\":\"response.output_text.delta\"}")]
    [InlineData("{\"type\":\"response.output_item.added\"}")]
    [InlineData("{\"type\":\"response.output_item.added\",\"item\":{\"type\":\"reasoning\"}}")]
    [InlineData("{\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{}\"}")]
    [InlineData("{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\"}")]
    [InlineData("{\"type\":\"response.function_call_arguments.done\",\"arguments\":\"{}\"}")]
    [InlineData("{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc_1\"}")]
    [InlineData("{\"type\":\"response.output_item.done\"}")]
    public void ParseEvent_KnownEventMissingRequiredShapeFailsClosed(string json) {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        Assert.Throws<InvalidDataException>(() => parser.ParseEvent(json, aggregator));
        Assert.False(parser.TerminalEventObserved);
    }
}
