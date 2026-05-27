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
    public void ParseEvent_FailedAndTopLevelErrorAppendErrors() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.failed","response":{"error":{"message":"stream failed"}}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"error":{"message":"bad input"}}
            """,
            aggregator
        );

        var result = aggregator.Build();

        Assert.Equal(["stream failed", "bad input"], result.Errors);
    }
}
