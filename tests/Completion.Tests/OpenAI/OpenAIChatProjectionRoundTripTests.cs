using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIChatProjectionRoundTripTests {
    private static readonly CompletionDescriptor DeepSeekInvocation = new(
        "api.deepseek.com",
        "openai-chat-v1",
        "deepseek-v4"
    );

    private static readonly ImmutableArray<ToolDefinition> WeatherTools = [
        new ToolDefinition(
            Name: "get_date",
            Description: "Get today's date.",
            Parameters: ImmutableArray<ToolParamSpec>.Empty
        ),
        new ToolDefinition(
            Name: "get_weather",
            Description: "Get weather by location and date.",
            Parameters: [
                new ToolParamSpec("location", "The city name.", ToolParamType.String),
                new ToolParamSpec("date", "The date in YYYY-MM-DD format.", ToolParamType.String)
            ]
        )
    ];

    [Fact]
    public void DeepSeekV4_ToolTurnResponse_RoundTripsToAssistantReplayMessage() {
        var parsed = ParseOpenAiActionMessage(
            WeatherTools,
            OpenAIChatDialects.DeepSeekV4,
            """
            {"choices":[{"index":0,"delta":{"reasoning_content":"I need tomorrow's date first. ","content":"","tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"reasoning_content":"Then I can query the weather.","content":null,"tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"content":null,"tool_calls":[{"id":"call_date_1","index":0,"type":"function","function":{"name":"get_date","arguments":"{}"}}]},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":null}
            """
        );

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(
            new CompletionRequest(
                ModelId: "deepseek-v4",
                SystemPrompt: string.Empty,
                Context: [
                    parsed,
                    new ToolResultsMessage(
                        Content: null,
                        Results: [
                            new ToolResult("get_date", "call_date_1", ToolExecutionStatus.Success, "2026-04-19")
                        ],
                        ExecuteError: null
                    )
                ],
                Tools: WeatherTools
            ),
            OpenAIChatDialects.DeepSeekV4
        );

        var assistantMessage = apiRequest.Messages[0];
        Assert.Equal("assistant", assistantMessage.Role);
        Assert.Equal("I need tomorrow's date first. Then I can query the weather.", assistantMessage.ReasoningContent);
        Assert.Null(assistantMessage.Content);

        var toolCall = Assert.Single(assistantMessage.ToolCalls!);
        Assert.Equal("call_date_1", toolCall.Id);
        Assert.Equal("get_date", toolCall.Function.Name);
        AssertJsonSemanticallyEqual("{}", toolCall.Function.Arguments);
    }

    [Fact]
    public void DeepSeekV4_FinalAnswerResponse_RoundTripsToAssistantReplayMessage() {
        var parsed = ParseOpenAiActionMessage(
            WeatherTools,
            OpenAIChatDialects.DeepSeekV4,
            """
            {"choices":[{"index":0,"delta":{"reasoning_content":"I have both date and weather. ","content":null,"tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"reasoning_content":"Now I can answer briefly.","content":null,"tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"content":"Hangzhou tomorrow will be cloudy, 7~13°C.","tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}
            """
        );

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(
            new CompletionRequest(
                ModelId: "deepseek-v4",
                SystemPrompt: string.Empty,
                Context: [parsed],
                Tools: WeatherTools
            ),
            OpenAIChatDialects.DeepSeekV4
        );

        var assistantMessage = Assert.Single(apiRequest.Messages);
        Assert.Equal("assistant", assistantMessage.Role);
        Assert.Equal("I have both date and weather. Now I can answer briefly.", assistantMessage.ReasoningContent);
        Assert.Equal("Hangzhou tomorrow will be cloudy, 7~13°C.", assistantMessage.Content);
        Assert.Null(assistantMessage.ToolCalls);
    }

    [Fact]
    public void DeepSeekV4_TwoSubTurns_RoundTripIntoNextRequestWithStableReplayOrder() {
        var toolTurn = ParseOpenAiActionMessage(
            WeatherTools,
            OpenAIChatDialects.DeepSeekV4,
            """
            {"choices":[{"index":0,"delta":{"reasoning_content":"Need tomorrow's date before weather.","content":"","tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"content":null,"tool_calls":[{"id":"call_date_1","index":0,"type":"function","function":{"name":"get_date","arguments":"{}"}}]},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":null}
            """
        );

        var finalAnswerTurn = ParseOpenAiActionMessage(
            WeatherTools,
            OpenAIChatDialects.DeepSeekV4,
            """
            {"choices":[{"index":0,"delta":{"reasoning_content":"Tomorrow is 2026-04-20. ","content":null,"tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"reasoning_content":"I can summarize now.","content":null,"tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"content":"Hangzhou tomorrow will be cloudy, 7~13°C.","tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}
            """
        );

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(
            new CompletionRequest(
                ModelId: "deepseek-v4",
                SystemPrompt: string.Empty,
                Context: [
                    new ObservationMessage("How's the weather in Hangzhou tomorrow?"),
                    toolTurn,
                    new ToolResultsMessage(
                        Content: null,
                        Results: [
                            new ToolResult("get_date", "call_date_1", ToolExecutionStatus.Success, "2026-04-19")
                        ],
                        ExecuteError: null
                    ),
                    finalAnswerTurn,
                    new ObservationMessage("What about Guangzhou tomorrow?")
                ],
                Tools: WeatherTools
            ),
            OpenAIChatDialects.DeepSeekV4
        );

        Assert.Collection(
            apiRequest.Messages,
            message => {
                Assert.Equal("user", message.Role);
                Assert.Equal("How's the weather in Hangzhou tomorrow?", message.Content);
            },
            message => {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("Need tomorrow's date before weather.", message.ReasoningContent);
                Assert.Null(message.Content);
                var toolCall = Assert.Single(message.ToolCalls!);
                Assert.Equal("call_date_1", toolCall.Id);
                Assert.Equal("get_date", toolCall.Function.Name);
            },
            message => {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call_date_1", message.ToolCallId);
                Assert.Contains("2026-04-19", message.Content, StringComparison.Ordinal);
            },
            message => {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("Tomorrow is 2026-04-20. I can summarize now.", message.ReasoningContent);
                Assert.Equal("Hangzhou tomorrow will be cloudy, 7~13°C.", message.Content);
                Assert.Null(message.ToolCalls);
            },
            message => {
                Assert.Equal("user", message.Role);
                Assert.Equal("What about Guangzhou tomorrow?", message.Content);
            }
        );
    }

    [Fact]
    public void StrictOpenAi_RoundTripRemainsIntentionallyLossyForReasoningContent() {
        var parsed = ParseOpenAiActionMessage(
            WeatherTools,
            OpenAIChatDialects.DeepSeekV4,
            """
            {"choices":[{"index":0,"delta":{"reasoning_content":"Private chain of thought.","content":null,"tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{"content":"Visible answer.","tool_calls":null},"finish_reason":null}],"usage":null}
            """,
            """
            {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}
            """
        );

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(
            new CompletionRequest(
                ModelId: "gpt-4.1",
                SystemPrompt: string.Empty,
                Context: [parsed],
                Tools: WeatherTools
            ),
            OpenAIChatDialects.Strict
        );

        var assistantMessage = Assert.Single(apiRequest.Messages);
        Assert.Equal("assistant", assistantMessage.Role);
        Assert.Equal("Visible answer.", assistantMessage.Content);
        Assert.Null(assistantMessage.ReasoningContent);
    }

    private static ActionMessage ParseOpenAiActionMessage(
        ImmutableArray<ToolDefinition> tools,
        OpenAIChatDialect dialect,
        params string[] events
    ) {
        var parser = new OpenAIChatStreamParser(tools, dialect.WhitespaceContentMode, dialect.ReasoningMode);
        var aggregator = new CompletionAggregator(DeepSeekInvocation);

        foreach (var e in events) {
            parser.ParseEvent(e, aggregator);
        }

        parser.Complete(aggregator);
        return aggregator.Build().Message;
    }

    private static void AssertJsonSemanticallyEqual(string expectedJson, string actualJson) {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        Assert.True(JsonElementDeepEquals(expected.RootElement, actual.RootElement));
    }

    private static bool JsonElementDeepEquals(JsonElement left, JsonElement right) {
        if (left.ValueKind != right.ValueKind) {
            return false;
        }

        switch (left.ValueKind) {
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                var rightProperties = right.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();

                if (leftProperties.Length != rightProperties.Length) {
                    return false;
                }

                for (var i = 0; i < leftProperties.Length; i++) {
                    if (!string.Equals(leftProperties[i].Name, rightProperties[i].Name, StringComparison.Ordinal)) {
                        return false;
                    }

                    if (!JsonElementDeepEquals(leftProperties[i].Value, rightProperties[i].Value)) {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();

                if (leftItems.Length != rightItems.Length) {
                    return false;
                }

                for (var i = 0; i < leftItems.Length; i++) {
                    if (!JsonElementDeepEquals(leftItems[i], rightItems[i])) {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Number:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

            default:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
        }
    }
}
