using System.Collections.Immutable;
using System.Net;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.Anthropic.Tests;

public sealed class AnthropicClientTests {
    [Fact]
    public void Constructor_RequiresPreconfiguredHttpClientBaseAddress() {
        using var handler = new EmptyHttpMessageHandler();
        using var httpClient = new HttpClient(handler);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new AnthropicClient(apiKey: null, httpClient: httpClient)
        );

        Assert.Contains("HttpClient.BaseAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_UsesPreconfiguredHttpClientBaseAddress() {
        using var handler = new EmptyHttpMessageHandler();
        var preconfigured = new Uri("http://localhost:9000/");
        using var httpClient = new HttpClient(handler) {
            BaseAddress = preconfigured
        };

        var client = new AnthropicClient(apiKey: null, httpClient: httpClient);

        Assert.NotNull(client);
        Assert.Equal(preconfigured, httpClient.BaseAddress);
        Assert.Equal(preconfigured.Host, client.Name);
    }

    [Fact]
    public void Constructor_RejectsBaseAddressWithoutTrailingSlash() {
        using var handler = new EmptyHttpMessageHandler();
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:9000/anthropic")
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => new AnthropicClient(apiKey: null, httpClient: httpClient)
        );

        Assert.Contains("end with '/'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_EarlyStopAfterReasoningDelta_BalancesThinkingLifecycleWithoutReturningUsage() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    event: message_start
                    data: {"type":"message_start","message":{"usage":{"input_tokens":17,"output_tokens":0}}}

                    event: content_block_start
                    data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

                    event: content_block_delta
                    data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"partial"}}

                    event: content_block_stop
                    data: {"type":"content_block_stop","index":0}

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

    [Fact]
    public async Task StreamCompletionAsync_MessageStopReturnsWithoutReadingLaterFramesAndSendsSseAccept() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                event: message_start
                data: {"type":"message_start","message":{}}

                event: ping
                data: {"type":"ping"}

                event: future_progress
                data: {"type":"future_progress","phase":"reasoning"}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"done"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

                event: message_stop
                data: {"type":"message_stop"}

                event: message_start
                data: {not-json}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        var result = await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        Assert.Equal("done", result.Message.GetFlattenedText());
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        Assert.Equal("end_turn", result.Termination.ProviderReason);
        Assert.Equal("text/event-stream", Assert.Single(handler.RequestAcceptHeaders));
    }

    [Fact]
    public async Task StreamCompletionAsync_ErrorEventIsTerminalAndPreservesProviderReason() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                event: error
                data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}

                event: message_start
                data: {not-json}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        var result = await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal("overloaded_error", result.Termination.ProviderReason);
        Assert.Equal("Overloaded", result.Termination.Detail);
        Assert.Equal(["Overloaded"], result.Errors);
    }

    [Fact]
    public async Task StreamCompletionAsync_EofBeforeTerminalEventIsUncertainInterruption() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                event: ping
                data: {"type":"ping"}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        var exception = await Assert.ThrowsAsync<CompletionStreamInterruptedException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Equal("Anthropic Messages", exception.StreamDisplayName);
    }

    [Theory]
    [InlineData("data: {\"type\":\"ping\"}\n\n")]
    [InlineData("event: message_start\ndata: {\"type\":\"ping\"}\n\n")]
    [InlineData("event: message_start\ndata: {not-json}\n\n")]
    [InlineData("event: message_stop\ndata: [DONE]\n\n")]
    public async Task StreamCompletionAsync_RejectsUnnamedMismatchedMalformedOrDoneFrames(
        string body
    ) {
        var handler = new SequenceHttpMessageHandler(EventStreamResponse(body));
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_InterruptionClosesObserverThinkingLifecycle() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"partial"}}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);
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
    public async Task StreamCompletionAsync_ReadExceptionPropagatesWithoutBeingReclassified() {
        var expected = new IOException("scripted read failure");
        var response = new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StreamContent(new ThrowAfterPayloadStream(
                """
                event: ping
                data: {"type":"ping"}

                """u8.ToArray(),
                expected
            ))
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "text/event-stream"
        );
        var handler = new SequenceHttpMessageHandler(response);
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        var actual = await Assert.ThrowsAsync<IOException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task StreamCompletionAsync_CallerCancellationPreservesToken() {
        var stream = new CancellationWaitingStream();
        var response = new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "text/event-stream"
        );
        var handler = new SequenceHttpMessageHandler(response);
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);
        using var cancellation = new CancellationTokenSource();

        var call = client.StreamCompletionAsync(CreateRequest(), null, cancellation.Token);
        await stream.ReadStarted;
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task StreamCompletionAsync_SuccessWithNonEventStreamMediaTypeIsProtocolError() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            }
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Contains("text/event-stream", exception.Message, StringComparison.Ordinal);
        Assert.Contains("application/json", exception.Message, StringComparison.Ordinal);
    }

    private static CompletionRequest CreateRequest() {
        return new CompletionRequest(
            ModelId: "claude-opus-4-6",
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

        public List<string> RequestAcceptHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestAcceptHeaders.Add(request.Headers.Accept.ToString());
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ThrowAfterPayloadStream : Stream {
        private readonly byte[] _payload;
        private readonly IOException _exception;
        private int _position;

        public ThrowAfterPayloadStream(byte[] payload, IOException exception) {
            _payload = payload;
            _exception = exception;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            if (_position >= _payload.Length) { throw _exception; }
            int bytesToCopy = Math.Min(count, _payload.Length - _position);
            _payload.AsSpan(_position, bytesToCopy).CopyTo(buffer.AsSpan(offset, bytesToCopy));
            _position += bytesToCopy;
            return bytesToCopy;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= _payload.Length) {
                return ValueTask.FromException<int>(_exception);
            }

            int bytesToCopy = Math.Min(buffer.Length, _payload.Length - _position);
            _payload.AsMemory(_position, bytesToCopy).CopyTo(buffer);
            _position += bytesToCopy;
            return ValueTask.FromResult(bytesToCopy);
        }

        public override void Flush() {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationWaitingStream : Stream {
        private readonly TaskCompletionSource _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) {
            _readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() {
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
