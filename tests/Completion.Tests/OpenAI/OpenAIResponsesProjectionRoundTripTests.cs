using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesProjectionRoundTripTests {
    private static readonly CompletionDescriptor OpenAIInvocation = new(
        "openai",
        "openai-responses-v1",
        "gpt-5"
    );

    [Fact]
    public void ReasoningAndToolCallReplayBeforeToolResultAndNextUserTurn() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(OpenAIInvocation);

        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"Need tool."}],"encrypted_content":"enc_123"}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"city\":\"Paris\"}","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather"}}
            """,
            aggregator
        );
        parser.ParseEvent("""{"type":"response.completed"}""", aggregator);

        var previousAssistantTurn = aggregator.Build().Message;
        var request = new CompletionRequest(
            ModelId: "gpt-5",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("What's the weather in Paris?"),
                previousAssistantTurn,
                new ToolResultsMessage(
                    content: null,
                    results: [
                        ToolResult.FromText("get_weather", "call_123", ToolExecutionStatus.Success, "Sunny")
                    ]
                ),
                new ObservationMessage("What should I wear?")
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var apiRequest = OpenAIResponsesMessageConverter.ConvertToApiRequest(request);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(apiRequest, apiRequest.GetType()));

        Assert.Collection(
            json.RootElement.GetProperty("input").EnumerateArray().ToArray(),
            item => {
                Assert.Equal("message", item.GetProperty("type").GetString());
                Assert.Equal("user", item.GetProperty("role").GetString());
                Assert.Equal("What's the weather in Paris?", item.GetProperty("content")[0].GetProperty("text").GetString());
            },
            item => {
                Assert.Equal("reasoning", item.GetProperty("type").GetString());
                Assert.Equal("rs_1", item.GetProperty("id").GetString());
                Assert.Equal("enc_123", item.GetProperty("encrypted_content").GetString());
            },
            item => {
                Assert.Equal("function_call", item.GetProperty("type").GetString());
                Assert.Equal("call_123", item.GetProperty("call_id").GetString());
                Assert.Equal("get_weather", item.GetProperty("name").GetString());
                Assert.Equal("{\"city\":\"Paris\"}", item.GetProperty("arguments").GetString());
            },
            item => {
                Assert.Equal("function_call_output", item.GetProperty("type").GetString());
                Assert.Equal("call_123", item.GetProperty("call_id").GetString());
                Assert.Equal("Sunny", item.GetProperty("output").GetString());
            },
            item => {
                Assert.Equal("message", item.GetProperty("type").GetString());
                Assert.Equal("user", item.GetProperty("role").GetString());
                Assert.Equal("What should I wear?", item.GetProperty("content")[0].GetProperty("text").GetString());
            }
        );
    }
}
