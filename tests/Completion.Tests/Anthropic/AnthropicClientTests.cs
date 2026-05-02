using System.Net;
using System.Text;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Anthropic.Tests;

public sealed class AnthropicClientTests {
    [Fact]
    public void Constructor_DoesNotOverwriteExternalBaseAddressWhenNoneProvided() {
        using var handler = new EmptyHttpMessageHandler();
        var preconfigured = new Uri("http://localhost:9000/");
        using var httpClient = new HttpClient(handler) {
            BaseAddress = preconfigured
        };

        var client = new AnthropicClient(apiKey: null, httpClient: httpClient, baseAddress: null);

        Assert.NotNull(client);
        Assert.Equal(preconfigured, httpClient.BaseAddress);
    }

    [Fact]
    public void Constructor_ExplicitBaseAddressOverridesExternalHttpClientBaseAddress() {
        using var handler = new EmptyHttpMessageHandler();
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:9000/")
        };
        var explicitAddress = new Uri("http://localhost:7777/");

        var client = new AnthropicClient(apiKey: null, httpClient: httpClient, baseAddress: explicitAddress);

        Assert.NotNull(client);
        Assert.Equal(explicitAddress, httpClient.BaseAddress);
    }

    [Fact]
    public async Task StreamCompletionAsync_EarlyStopAfterReasoningDelta_BalancesThinkingLifecycleWithoutReturningUsage() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"type":"message_start","message":{"usage":{"input_tokens":17,"output_tokens":0}}}

                    data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

                    data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"partial"}}

                    data: {"type":"content_block_stop","index":0}

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

        var client = new AnthropicClient(apiKey: null, httpClient: httpClient);
        var observer = new CompletionStreamObserver();
        var thinkingBeginCount = 0;
        var thinkingEndCount = 0;
        var reasoningDeltaCount = 0;
        observer.ReceivedThinkingBegin += () => thinkingBeginCount++;
        observer.ReceivedThinkingEnd += () => thinkingEndCount++;
        observer.ReceivedReasoningDelta += delta => {
            reasoningDeltaCount++;
            Assert.Equal("partial", delta);
            observer.ShouldStop = true;
        };

        var aggregated = await client.StreamCompletionAsync(
            new CompletionRequest(
                ModelId: "claude-3-5-sonnet-20241022",
                SystemPrompt: "system",
                Context: new[] { new ObservationMessage("hello") },
                Tools: System.Collections.Immutable.ImmutableArray<ToolDefinition>.Empty
            ),
            observer,
            CancellationToken.None
        );

        Assert.Equal(1, thinkingBeginCount);
        Assert.Equal(1, thinkingEndCount);
        Assert.Equal(1, reasoningDeltaCount);
        Assert.DoesNotContain(aggregated.Message.Blocks, block => block.Kind == ActionBlockKind.Thinking);
        var text = Assert.Single(aggregated.Message.Blocks);
        Assert.Equal(string.Empty, Assert.IsType<ActionBlock.Text>(text).Content);
    }

    [Fact]
    public async Task StreamCompletionAsync_NonSuccessStatus_IncludesResponseBodySnippetInException() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent(
                    """
                    {"type":"error","error":{"type":"invalid_request_error","message":"bad input"}}
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new AnthropicClient(apiKey: null, httpClient: httpClient);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.StreamCompletionAsync(
                new CompletionRequest(
                    ModelId: "claude-3-5-sonnet-20241022",
                    SystemPrompt: "system",
                    Context: new[] { new ObservationMessage("hello") },
                    Tools: System.Collections.Immutable.ImmutableArray<ToolDefinition>.Empty
                ),
                observer: null,
                CancellationToken.None
            )
        );

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("bad input", exception.Message, StringComparison.Ordinal);
    }

    private sealed class EmptyHttpMessageHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHttpMessageHandler(params HttpResponseMessage[] responses) {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
