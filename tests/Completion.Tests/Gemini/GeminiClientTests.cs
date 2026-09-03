using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.Gemini.Tests;

public sealed class GeminiClientTests {
    [Fact]
    public async Task StreamCompletionAsync_CapturesUsageChunkAfterFinishReason() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}

                data: {"usageMetadata":{"promptTokenCount":100,"cachedContentTokenCount":70,"candidatesTokenCount":5},"candidates":[]}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        CompletionResult result = await client.StreamCompletionAsync(
            CreateRequest(),
            new CompletionInvocationOptions {
                PromptCacheReuseHint = PromptCacheReuseHint.ReuseExpectedSoon
            },
            observer: null,
            CancellationToken.None
        );

        Assert.Equal("ok", result.Message.GetFlattenedText());
        Assert.Equal(30, result.Usage.UncachedInputTokens);
        Assert.Equal(70, result.Usage.CacheReadInputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal(
            PromptCacheRequestStatus.Requested,
            result.Usage.PromptCache.RequestStatus
        );
        Assert.Equal(
            PromptCacheSupportStatus.Unknown,
            result.Usage.PromptCache.SupportStatus
        );
        Assert.Equal(
            PromptCacheObservationStatus.Partial,
            result.Usage.PromptCache.ObservationStatus
        );
    }

    [Fact]
    public void Constructor_RequiresPreconfiguredHttpClientBaseAddress() {
        if (!GeminiProductionTypesPresent()) { return; }

        using var handler = new EmptyHttpMessageHandler();
        using var httpClient = new HttpClient(handler);

        var exception = Assert.Throws<InvalidOperationException>(() => CreateGeminiClient(httpClient));

        Assert.Contains("HttpClient.BaseAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_UsesPreconfiguredHttpClientBaseAddress() {
        if (!GeminiProductionTypesPresent()) { return; }

        using var handler = new EmptyHttpMessageHandler();
        var preconfigured = new Uri("http://localhost:9000/");
        using var httpClient = new HttpClient(handler) {
            BaseAddress = preconfigured
        };

        dynamic client = CreateGeminiClient(httpClient);

        Assert.NotNull(client);
        Assert.Equal(preconfigured, httpClient.BaseAddress);
        Assert.Equal(preconfigured.Host, (string)client.Name);
    }

    [Fact]
    public void Constructor_RejectsBaseAddressWithoutTrailingSlash() {
        if (!GeminiProductionTypesPresent()) { return; }

        using var handler = new EmptyHttpMessageHandler();
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:9000/gemini")
        };

        var exception = Assert.Throws<InvalidOperationException>(() => CreateGeminiClient(httpClient));

        Assert.Contains("end with '/'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_NonSuccessStatus_IncludesResponseBodySnippetInException() {
        if (!GeminiProductionTypesPresent()) { return; }

        using var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent(
                    """
                    {"error":{"message":"bad input","status":"INVALID_ARGUMENT"}}
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            }
        );
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = CreateGeminiClient(httpClient);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => InvokeStreamCompletionAsync(client, CreateRequest())
        );

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("bad input", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_UsesApiKeyHeaderWithoutLeakingKeyIntoRequestUri() {
        if (!GeminiProductionTypesPresent()) { return; }

        using var handler = new InspectingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}

                    data: {"usageMetadata":{"promptTokenCount":10,"cachedContentTokenCount":0,"candidatesTokenCount":2},"candidates":[]}

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = CreateGeminiClient(httpClient, apiKey: "secret-key");
        var result = await InvokeStreamCompletionAsync(client, CreateRequest());

        Assert.Equal("ok", result.Message.GetFlattenedText());
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        Assert.Equal("secret-key", handler.LastRequest?.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("secret-key", handler.LastRequest?.RequestUri?.ToString(), StringComparison.Ordinal);
        Assert.Equal("text/event-stream", handler.LastRequest?.Headers.Accept.ToString());
    }

    [Fact]
    public async Task StreamCompletionAsync_PromptBlockIsTerminalAndReturnsWithoutReadingLaterFrames() {
        using var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"promptFeedback":{"blockReason":"SAFETY","futurePromptField":{"message":"ignored"}}}

                data: {not-json}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        var result = await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        Assert.Equal(CompletionTerminationKind.Incomplete, result.Termination.Kind);
        Assert.Equal("SAFETY", result.Termination.ProviderReason);
        Assert.Contains("SAFETY", result.Termination.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_ErrorEnvelopeIsTerminalAndReturnsWithoutReadingLaterFrames() {
        using var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                event: future-gemini-event
                data: {"error":{"code":503,"message":"Unavailable","status":"UNAVAILABLE"}}

                data: {not-json}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        var result = await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal("UNAVAILABLE", result.Termination.ProviderReason);
        Assert.Equal("Unavailable", result.Termination.Detail);
        Assert.Equal(["Unavailable"], result.Errors);
    }

    [Fact]
    public async Task StreamCompletionAsync_NoArgFunctionCallCanCompleteAtProviderTerminal() {
        using var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"no_args\",\"id\":\"call-1\"}}]},\"finishReason\":\"STOP\"}]}\n\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        var result = await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        var toolCall = Assert.Single(result.Message.ToolCalls);
        Assert.Equal("call-1", toolCall.ToolCallId);
        Assert.Equal("no_args", toolCall.ToolName);
        Assert.Equal("{}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public async Task StreamCompletionAsync_EofBeforeProviderTerminalIsUncertainInterruption() {
        using var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"candidates":[{"content":{"role":"model","parts":[{"text":"partial"}]}}]}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        var exception = await Assert.ThrowsAsync<CompletionStreamInterruptedException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Equal("Gemini streamGenerateContent", exception.StreamDisplayName);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {not-json}\n\n")]
    [InlineData("data: []\n\n")]
    [InlineData("data: {\"candidates\":7}\n\n")]
    public async Task StreamCompletionAsync_RejectsDoneMalformedOrInvalidKnownShapes(string body) {
        using var handler = new SequenceHttpMessageHandler(EventStreamResponse(body));
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_ObserverEarlyStopReturnsLocalIncompleteResult() {
        using var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"candidates":[{"content":{"role":"model","parts":[{"text":"first"}]}}]}

                data: {"candidates":[{"content":{"role":"model","parts":[{"text":" second"}]}},"finishReason":"STOP"}]}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);
        var observer = new CompletionStreamObserver();
        observer.ReceivedTextDelta += _ => observer.ShouldStop = true;

        var result = await client.StreamCompletionAsync(CreateRequest(), observer, CancellationToken.None);

        Assert.Equal("first", result.Message.GetFlattenedText());
        Assert.Equal(CompletionTerminationKind.Incomplete, result.Termination.Kind);
        Assert.Contains("observer stopped", result.Termination.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_ReadExceptionPropagatesWithoutBeingReclassified() {
        var expected = new IOException("scripted read failure");
        var response = new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StreamContent(
                new ThrowAfterPayloadStream(
                    "data: {\"usageMetadata\":{}}\n\n"u8.ToArray(),
                    expected
                )
            )
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "text/event-stream"
        );
        using var handler = new SequenceHttpMessageHandler(response);
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

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
        using var handler = new SequenceHttpMessageHandler(response);
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);
        using var cancellation = new CancellationTokenSource();

        var call = client.StreamCompletionAsync(CreateRequest(), null, cancellation.Token);
        await stream.ReadStarted;
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task StreamCompletionAsync_SuccessWithNonEventStreamMediaTypeIsProtocolError() {
        using var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            }
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Contains("text/event-stream", exception.Message, StringComparison.Ordinal);
        Assert.Contains("application/json", exception.Message, StringComparison.Ordinal);
    }

    private static CompletionRequest CreateRequest() {
        return new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                "system",
                CompletionOutputContract.ProviderDefault(System.Collections.Immutable.ImmutableArray<ToolDefinition>.Empty),
                new[] { new ObservationMessage("hello") }
            ),
            tailMessages: []
        );
    }

    private static object CreateGeminiClient(HttpClient httpClient, string? apiKey = null) {
        var clientType = typeof(CompletionHttpTransportFactory).Assembly.GetType("Atelia.Completion.Gemini.GeminiClient");
        Assert.NotNull(clientType);
        var constructor = clientType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(HasSupportedGeminiConstructorShape);

        Assert.NotNull(constructor);

        var arguments = constructor!
            .GetParameters()
            .Select(parameter => ResolveConstructorArgument(parameter, httpClient, apiKey))
            .ToArray();

        try {
            return constructor.Invoke(arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static bool HasSupportedGeminiConstructorShape(ConstructorInfo constructor) {
        var parameters = constructor.GetParameters();
        return parameters.Any(parameter => parameter.ParameterType == typeof(HttpClient))
            && parameters.All(
                parameter => parameter.ParameterType == typeof(HttpClient)
                    || (parameter.ParameterType == typeof(string) && string.Equals(parameter.Name, "apiKey", StringComparison.OrdinalIgnoreCase))
            );
    }

    private static object? ResolveConstructorArgument(ParameterInfo parameter, HttpClient httpClient, string? apiKey) {
        if (parameter.ParameterType == typeof(HttpClient)) { return httpClient; }

        if (parameter.ParameterType == typeof(string) && string.Equals(parameter.Name, "apiKey", StringComparison.OrdinalIgnoreCase)) { return apiKey; }

        if (parameter.HasDefaultValue) { return parameter.DefaultValue; }

        throw new InvalidOperationException(
            $"Unsupported GeminiClient constructor parameter '{parameter.Name}' of type '{parameter.ParameterType}'."
        );
    }

    private static async Task<CompletionResult> InvokeStreamCompletionAsync(object client, CompletionRequest request) {
        var method = client.GetType().GetMethod(
            "StreamCompletionAsync",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(CompletionRequest), typeof(CompletionStreamObserver), typeof(CancellationToken) },
            modifiers: null
        );

        Assert.NotNull(method);

        var task = (Task)method!.Invoke(client, new object?[] { request, null, CancellationToken.None })!;
        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        Assert.NotNull(resultProperty);
        return Assert.IsType<CompletionResult>(resultProperty!.GetValue(task));
    }

    private static bool GeminiProductionTypesPresent() {
        var assembly = typeof(CompletionHttpTransportFactory).Assembly;
        return assembly.GetType("Atelia.Completion.Gemini.GeminiClient") is not null;
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

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.Method == HttpMethod.Get) {
                return Task.FromResult(ModelInfoResponse());
            }
            RequestCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class InspectingHttpMessageHandler : HttpMessageHandler {
        private readonly HttpResponseMessage _response;

        public InspectingHttpMessageHandler(HttpResponseMessage response) {
            _response = response;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.Method == HttpMethod.Get) {
                return Task.FromResult(ModelInfoResponse());
            }
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }

    private static HttpResponseMessage ModelInfoResponse(
        int maximumTokens = 65_536
    ) => new(HttpStatusCode.OK) {
        Content = new StringContent(
            $"{{\"name\":\"models/gemini-2.5-flash\",\"outputTokenLimit\":{maximumTokens}}}",
            Encoding.UTF8,
            "application/json"
        )
    };

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
