using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesClientTests {
    [Fact]
    public async Task StreamCompletionAsync_SendsResponsesRequestAndParsesStreamingBlocks() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    event: response.output_item.done
                    data: {"type":"response.output_item.done","item":{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"Need tool."}],"encrypted_content":"abc"}}

                    event: response.function_call_arguments.delta
                    data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"city\":\"Par"}

                    event: response.function_call_arguments.done
                    data: {"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"city\":\"Paris\"}","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather"}}

                    event: response.output_text.delta
                    data: {"type":"response.output_text.delta","delta":"Sunny."}

                    event: response.completed
                    data: {"type":"response.completed"}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIResponsesClient(apiKey: null, httpClient: httpClient);
        var request = new CompletionRequest(
            ModelId: "gpt-5",
            SystemPrompt: "system",
            Context: new IHistoryMessage[] {
                new ObservationMessage("hello")
            },
            Tools: ImmutableArray.Create(
                new ToolDefinition(
                    "get_weather",
                    "Look up weather.",
                    new ToolSchema.Object(
                        properties: [
                            new ToolSchema.Property(
                                "city",
                                new ToolSchema.Value(ToolParamType.String, description: "City name."),
                                isRequired: true
                            )
                        ]
                    )
                )
            )
        );

        var result = await client.StreamCompletionAsync(request, null, CancellationToken.None);

        Assert.Equal("http://localhost:8000/v1/responses", Assert.Single(handler.RequestUris));
        Assert.Equal("text/event-stream", Assert.Single(handler.RequestAcceptHeaders));

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = document.RootElement;
        Assert.Equal("gpt-5", root.GetProperty("model").GetString());
        Assert.Equal("system", root.GetProperty("instructions").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("reasoning.encrypted_content", root.GetProperty("include")[0].GetString());

        var input = root.GetProperty("input");
        var userMessage = Assert.Single(input.EnumerateArray());
        Assert.Equal("message", userMessage.GetProperty("type").GetString());
        Assert.Equal("user", userMessage.GetProperty("role").GetString());
        Assert.Equal("input_text", userMessage.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("hello", userMessage.GetProperty("content")[0].GetProperty("text").GetString());

        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("get_weather", tool.GetProperty("name").GetString());
        Assert.True(tool.GetProperty("strict").GetBoolean());

        Assert.Collection(
            result.Message.Blocks,
            block => Assert.IsType<OpenAIResponsesReasoningBlock>(block),
            block => Assert.Equal("call_123", Assert.IsType<ActionBlock.ToolCall>(block).Call.ToolCallId),
            block => Assert.Equal("Sunny.", Assert.IsType<ActionBlock.Text>(block).Content)
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_EarlyStop_DoesNotFlushIncompleteFunctionCalls() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"city\":\"Par"}

                    data: {"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"city\":\"Paris\"}","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather"}}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIResponsesClient(apiKey: null, httpClient: httpClient);
        var observer = new CompletionStreamObserver { ShouldStop = true };

        var result = await client.StreamCompletionAsync(CreateRequest(), observer, CancellationToken.None);

        Assert.DoesNotContain(result.Message.Blocks, block => block.Kind == ActionBlockKind.ToolCall);
        Assert.Equal(string.Empty, Assert.IsType<ActionBlock.Text>(Assert.Single(result.Message.Blocks)).Content);
    }

    [Fact]
    public async Task StreamCompletionAsync_UsesNormalizedBaseAddressFromTransportFactory() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"type":"response.output_text.delta","delta":"ok"}

                    data: {"type":"response.completed"}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
            new Uri("http://localhost:8000/prefix"),
            handler
        );
        var client = new OpenAIResponsesClient(apiKey: null, httpClient: httpClient);

        var result = await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        Assert.Equal("ok", result.Message.GetFlattenedText());
        Assert.Equal(new Uri("http://localhost:8000/prefix/"), httpClient.BaseAddress);
        Assert.Equal("http://localhost:8000/prefix/v1/responses", Assert.Single(handler.RequestUris));
    }

    [Fact]
    public async Task StreamCompletionAsync_UsesMessageConverterForReasoningReplayContinuity() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"type":"response.output_text.delta","delta":"done"}

                    data: {"type":"response.completed"}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIResponsesClient(apiKey: null, httpClient: httpClient);
        var request = new CompletionRequest(
            ModelId: "gpt-5",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
                new ObservationMessage("What's the weather in Paris?"),
                new ActionMessage(
                    [
                        new OpenAIResponsesReasoningBlock(
                            """{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"Need tool."}],"encrypted_content":"enc_123"}""",
                            new CompletionDescriptor("openai", "openai-responses-v1", "gpt-5"),
                            "Need tool."
                        ),
                        new ActionBlock.ToolCall(new RawToolCall("get_weather", "call_123", """{"city":"Paris"}"""))
                    ]
                ),
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

        var result = await client.StreamCompletionAsync(request, null, CancellationToken.None);

        Assert.Equal("done", result.Message.GetFlattenedText());

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Collection(
            document.RootElement.GetProperty("input").EnumerateArray().ToArray(),
            item => Assert.Equal("message", item.GetProperty("type").GetString()),
            item => {
                Assert.Equal("reasoning", item.GetProperty("type").GetString());
                Assert.Equal("enc_123", item.GetProperty("encrypted_content").GetString());
            },
            item => {
                Assert.Equal("function_call", item.GetProperty("type").GetString());
                Assert.Equal("call_123", item.GetProperty("call_id").GetString());
            },
            item => {
                Assert.Equal("function_call_output", item.GetProperty("type").GetString());
                Assert.Equal("Sunny", item.GetProperty("output").GetString());
            },
            item => {
                Assert.Equal("message", item.GetProperty("type").GetString());
                Assert.Equal("What should I wear?", item.GetProperty("content")[0].GetProperty("text").GetString());
            }
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_EofBeforeTerminalEventIsUncertainInterruption() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"type":"response.output_text.delta","delta":"partial"}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIResponsesClient(null, httpClient);

        var exception = await Assert.ThrowsAsync<CompletionStreamInterruptedException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Equal("OpenAI Responses", exception.StreamDisplayName);
    }

    [Fact]
    public async Task StreamCompletionAsync_DoneBeforeTerminalEventIsUncertainInterruption() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"type":"response.output_text.delta","delta":"partial"}

                data: [DONE]

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIResponsesClient(null, httpClient);

        await Assert.ThrowsAsync<CompletionStreamInterruptedException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_ExplicitTerminalReturnsWithoutReadingLaterFrames() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                event: response.completed
                data: {"type":"response.completed"}

                data: {not-json}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIResponsesClient(null, httpClient);

        var result = await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
    }

    [Fact]
    public async Task StreamCompletionAsync_InterruptionClosesObserverThinkingLifecycle() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                event: response.output_item.added
                data: {"type":"response.output_item.added","item":{"id":"rs_1","type":"reasoning"}}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIResponsesClient(null, httpClient);
        var observer = new CompletionStreamObserver();
        var thinkingBeginCount = 0;
        var thinkingEndCount = 0;
        observer.ReceivedThinkingBegin += () => thinkingBeginCount++;
        observer.ReceivedThinkingEnd += () => thinkingEndCount++;

        await Assert.ThrowsAsync<CompletionStreamInterruptedException>(
            () => client.StreamCompletionAsync(CreateRequest(), observer, CancellationToken.None)
        );

        Assert.Equal(1, thinkingBeginCount);
        Assert.Equal(1, thinkingEndCount);
    }

    [Fact]
    public async Task StreamCompletionAsync_MalformedKnownEventFailsBeforeLaterTerminal() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                event: response.function_call_arguments.done
                data: {"type":"response.function_call_arguments.done","arguments":"{}"}

                event: response.completed
                data: {"type":"response.completed"}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIResponsesClient(null, httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Contains("item_id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_RejectsNamedSseEventAndErrorDataMismatch() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                event: response.completed
                data: {"type":"error","error":{"message":"bad input"}}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIResponsesClient(null, httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }

    private static CompletionRequest CreateRequest() {
        return new CompletionRequest(
            ModelId: "gpt-5",
            SystemPrompt: "system",
            Context: new[] { new ObservationMessage("hello") },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) {
        return new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };
    }

    private static HttpResponseMessage EventStreamResponse(string body) {
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHttpMessageHandler(params HttpResponseMessage[] responses) {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<string> RequestBodies { get; } = new();
        public List<string?> RequestUris { get; } = new();
        public List<string> RequestAcceptHeaders { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestUris.Add(request.RequestUri?.ToString());
            RequestAcceptHeaders.Add(request.Headers.Accept.ToString());

            if (request.Content is not null) {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            else {
                RequestBodies.Add(string.Empty);
            }

            return _responses.Dequeue();
        }
    }
}
