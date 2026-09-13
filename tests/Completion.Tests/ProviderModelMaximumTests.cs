using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.Gemini;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class ProviderModelMaximumTests {
    [Fact]
    public async Task Cache_OwnerCallerCancellationDoesNotCancelSharedFetch() {
        var fetchStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFetch = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var fetchCount = 0;
        CancellationToken fetchToken = default;
        var cache = new ProviderModelMaximumCache(async (_, token) => {
            Interlocked.Increment(ref fetchCount);
            fetchToken = token;
            fetchStarted.TrySetResult();
            return await releaseFetch.Task;
        });
        using var ownerCancellation = new CancellationTokenSource();

        Task<int> owner = cache.GetAsync("exact-model", ownerCancellation.Token);
        await fetchStarted.Task;
        Task<int> otherWaiter = cache.GetAsync(
            "exact-model",
            CancellationToken.None
        );
        ownerCancellation.Cancel();

        OperationCanceledException canceled = await Assert.ThrowsAnyAsync<
            OperationCanceledException
        >(() => owner);
        Assert.Equal(ownerCancellation.Token, canceled.CancellationToken);
        Assert.True(fetchToken.CanBeCanceled);
        Assert.False(fetchToken.IsCancellationRequested);
        Assert.False(otherWaiter.IsCompleted);
        releaseFetch.TrySetResult(200_000);
        Assert.Equal(200_000, await otherWaiter);
        Assert.Equal(
            200_000,
            await cache.GetAsync("exact-model", CancellationToken.None)
        );
        Assert.Equal(1, fetchCount);
    }

    [Fact]
    public async Task Cache_SoleWaiterCancellationCancelsAndEvictsSharedFetch() {
        var firstFetchStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstFetchObservedCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var fetchCount = 0;
        var cache = new ProviderModelMaximumCache(async (_, token) => {
            if (Interlocked.Increment(ref fetchCount) != 1) {
                return 65_536;
            }

            firstFetchStarted.TrySetResult();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException(
                    "The abandoned capability fetch returned."
                );
            }
            finally {
                if (token.IsCancellationRequested) {
                    firstFetchObservedCancellation.TrySetResult();
                }
            }
        });
        using var callerCancellation = new CancellationTokenSource();

        Task<int> first = cache.GetAsync(
            "exact-model",
            callerCancellation.Token
        );
        await firstFetchStarted.Task;
        callerCancellation.Cancel();
        OperationCanceledException canceled = await Assert.ThrowsAnyAsync<
            OperationCanceledException
        >(() => first);
        Assert.Equal(callerCancellation.Token, canceled.CancellationToken);
        await firstFetchObservedCancellation.Task;

        Assert.Equal(
            65_536,
            await cache.GetAsync("exact-model", CancellationToken.None)
        );
        Assert.Equal(2, fetchCount);
    }

    [Fact]
    public async Task Cache_FaultAutomaticallyEvictsBeforeNextCaller() {
        var fetchCount = 0;
        var cache = new ProviderModelMaximumCache((_, _) =>
            Interlocked.Increment(ref fetchCount) == 1
                ? Task.FromException<int>(new IOException("first fetch failed"))
                : Task.FromResult(65_536)
        );

        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(
            "exact-model",
            CancellationToken.None
        ));
        Assert.Equal(
            65_536,
            await cache.GetAsync("exact-model", CancellationToken.None)
        );
        Assert.Equal(
            65_536,
            await cache.GetAsync("exact-model", CancellationToken.None)
        );
        Assert.Equal(2, fetchCount);
    }

    [Fact]
    public async Task Cache_UnderlyingCancellationAutomaticallyEvicts() {
        var fetchCount = 0;
        using var underlyingCancellation = new CancellationTokenSource();
        underlyingCancellation.Cancel();
        var cache = new ProviderModelMaximumCache((_, _) =>
            Interlocked.Increment(ref fetchCount) == 1
                ? Task.FromCanceled<int>(underlyingCancellation.Token)
                : Task.FromResult(32_768)
        );

        OperationCanceledException canceled = await Assert.ThrowsAnyAsync<
            OperationCanceledException
        >(() => cache.GetAsync("exact-model", CancellationToken.None));
        Assert.Equal(
            underlyingCancellation.Token,
            canceled.CancellationToken
        );
        Assert.Equal(
            32_768,
            await cache.GetAsync("exact-model", CancellationToken.None)
        );
        Assert.Equal(2, fetchCount);
    }

    [Fact]
    public async Task Anthropic_ResolvesBeforePostAndCachesPerExactModelId() {
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get
                ? JsonResponse("{\"id\":\"model\",\"max_tokens\":123456}")
                : AnthropicCompletionResponse()
        ));
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(
            "anthropic-key",
            httpClient,
            apiVersion: "test-version"
        );

        _ = await client.StreamCompletionAsync(
            Request("claude/exact"),
            null,
            CancellationToken.None
        );
        _ = await client.StreamCompletionAsync(
            Request("claude/exact"),
            null,
            CancellationToken.None
        );
        _ = await client.StreamCompletionAsync(
            Request("claude-opus-5"),
            null,
            CancellationToken.None
        );

        RecordedRequest[] requests = handler.Requests.ToArray();
        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Post, HttpMethod.Post,
                HttpMethod.Get, HttpMethod.Post],
            requests.Select(static item => item.Method)
        );
        Assert.EndsWith(
            "/v1/models/claude%2Fexact",
            requests[0].Uri.AbsoluteUri,
            StringComparison.Ordinal
        );
        Assert.Equal(
            "anthropic-key",
            Assert.Single(requests[0].Headers["x-api-key"])
        );
        Assert.Equal(
            "test-version",
            Assert.Single(requests[0].Headers["anthropic-version"])
        );
        Assert.All(
            requests.Where(static item => item.Method == HttpMethod.Post),
            item => {
                using JsonDocument document = JsonDocument.Parse(item.Body!);
                Assert.Equal(
                    123_456,
                    document.RootElement.GetProperty("max_tokens").GetInt32()
                );
            }
        );
    }

    [Fact]
    public async Task Anthropic_PreFallbackFrozenIdentityBindsAndExecutesAfterCapability404() {
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.NotFound) {
                    Content = new StringContent("404 page not found")
                }
                : AnthropicCompletionResponse()
        ));
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);
        var connection = new CompletionConnectionConfig(
            Id: "opus4-6",
            Kind: "anthropic",
            ModelId: "claude-opus-4-6",
            CompletionSurfaceId: "anthropic",
            BaseAddress: "https://provider.example/"
        );
        using var registry = new CompletionConnectionRegistry(
            new CompletionConnectionsFileConfig([connection], connection.Id),
            new FixedClientFactory(client)
        );
        var frozen = CompletionDispatchIdentityFactory.Create(connection, client) with {
            // Golden identity from the model-info-only implementation before fallback.
            RequestAdapterFingerprint =
                "sha256:4d984ecac3a0632e43ba412b201bc541f7fa7d7b6ffcceeaba4f618338e5cba9"
        };

        var bound = Assert.IsType<CompletionDispatchBindingResult.Bound>(
            registry.BindExact(frozen)
        );
        CompletionResult result = await bound.Client.StreamCompletionAsync(
            Request(connection.ModelId), null, CancellationToken.None
        );

        Assert.True(result.Termination.IsSuccess);
        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Post],
            handler.Requests.Select(static request => request.Method)
        );
        using var body = JsonDocument.Parse(handler.Requests.Last().Body!);
        Assert.Equal(128_000, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData(404, "claude-opus-4-6", 128_000)]
    [InlineData(404, "claude-opus-4-7", 128_000)]
    [InlineData(404, "claude-opus-4-8", 128_000)]
    [InlineData(404, "claude-opus-5", 128_000)]
    [InlineData(404, "unknown-model", 32_768)]
    [InlineData(405, "claude-opus-5", 128_000)]
    [InlineData(501, "unknown-model", 32_768)]
    [InlineData(404, "claude-opus-5-custom", 32_768)]
    public async Task Anthropic_MissingModelsEndpointUsesCachedFallback(
        int status, string modelId, int expectedMaximum
    ) {
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get
                ? new HttpResponseMessage((HttpStatusCode)status) {
                    Content = new StringContent("404 page not found")
                }
                : AnthropicCompletionResponse()
        ));
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        for (int i = 0; i < 2; i++) {
            _ = await client.StreamCompletionAsync(
                Request(modelId), null, CancellationToken.None
            );
        }

        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Post, HttpMethod.Post],
            handler.Requests.Select(static request => request.Method)
        );
        foreach (var post in handler.Requests.Where(
            static request => request.Method == HttpMethod.Post
        )) {
            using var body = JsonDocument.Parse(post.Body!);
            Assert.Equal(expectedMaximum, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(modelId, body.RootElement.GetProperty("model").GetString());
        }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Anthropic_OtherCapabilityErrorsDoNotFallBack(int status) {
        var handler = new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)status)
        ));
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.StreamCompletionAsync(Request("claude-opus-5"), null, CancellationToken.None)
        );

        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"max_tokens\":null}")]
    [InlineData("{\"max_tokens\":0}")]
    [InlineData("{\"max_tokens\":1.0}")]
    [InlineData("{\"max_tokens\":\"1\"}")]
    [InlineData("{\"Max_Tokens\":1}")]
    [InlineData("{\"max_tokens\":1,\"max_tokens\":2}")]
    public async Task Anthropic_RejectsMalformedCapabilityBeforePost(
        string body
    ) {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(body))
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        InvalidDataException exception = await Assert.ThrowsAsync<
            InvalidDataException
        >(() => client.StreamCompletionAsync(
            Request("claude-bad"),
            null,
            CancellationToken.None
        ));

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.DoesNotContain(body, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_StatusAndOversizeFailuresDoNotLeakOrPoisonCache() {
        const string KeyCanary = "ANTHROPIC_KEY_CANARY";
        const string BodyCanary = "ANTHROPIC_BODY_CANARY";
        var getCount = 0;
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? AnthropicCompletionResponse()
                : Interlocked.Increment(ref getCount) switch {
                    1 => new HttpResponseMessage(HttpStatusCode.BadRequest) {
                        Content = new StringContent(BodyCanary)
                    },
                    2 => JsonResponse(new string(
                        'x',
                        ProviderModelCapabilityResponse.MaximumResponseBytes + 1
                    )),
                    _ => JsonResponse("{\"max_tokens\":200000}")
                }
        ));
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(KeyCanary, httpClient);

        HttpRequestException status = await Assert.ThrowsAsync<
            HttpRequestException
        >(() => client.StreamCompletionAsync(
            Request("claude-retry"),
            null,
            CancellationToken.None
        ));
        InvalidDataException oversize = await Assert.ThrowsAsync<
            InvalidDataException
        >(() => client.StreamCompletionAsync(
            Request("claude-retry"),
            null,
            CancellationToken.None
        ));
        _ = await client.StreamCompletionAsync(
            Request("claude-retry"),
            null,
            CancellationToken.None
        );

        string failures = status + oversize.ToString();
        Assert.DoesNotContain(KeyCanary, failures, StringComparison.Ordinal);
        Assert.DoesNotContain(BodyCanary, failures, StringComparison.Ordinal);
        Assert.Equal(3, getCount);
        Assert.Equal(HttpMethod.Post, handler.Requests.Last().Method);
    }

    [Fact]
    public async Task Anthropic_ConcurrentFetchIsOneFlight() {
        var fetchStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFetch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var getCount = 0;
        var handler = new RecordingHandler(async (request, cancellationToken) => {
            if (request.Method == HttpMethod.Post) {
                return AnthropicCompletionResponse();
            }
            int count = Interlocked.Increment(ref getCount);
            if (count == 1) {
                fetchStarted.TrySetResult();
                await releaseFetch.Task.WaitAsync(cancellationToken);
            }
            return JsonResponse("{\"max_tokens\":200000}");
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);

        Task<CompletionResult>[] calls = Enumerable.Range(0, 8)
            .Select(_ => client.StreamCompletionAsync(
                Request("claude-concurrent"),
                null,
                CancellationToken.None
            ))
            .ToArray();
        await fetchStarted.Task;
        Assert.Equal(1, getCount);
        releaseFetch.TrySetResult();
        await Task.WhenAll(calls);
        Assert.Equal(1, getCount);
    }

    [Fact]
    public async Task Gemini_ResolvesBeforePostAndCachesPerExactModelId() {
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get
                ? JsonResponse("{\"name\":\"model\",\"outputTokenLimit\":65536}")
                : GeminiCompletionResponse()
        ));
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient("gemini-key", httpClient);

        _ = await client.StreamCompletionAsync(
            Request("gemini/exact"),
            null,
            CancellationToken.None
        );
        _ = await client.StreamCompletionAsync(
            Request("gemini/exact"),
            null,
            CancellationToken.None
        );
        _ = await client.StreamCompletionAsync(
            Request("models/gemini-other"),
            null,
            CancellationToken.None
        );

        RecordedRequest[] requests = handler.Requests.ToArray();
        Assert.Equal(
            [HttpMethod.Get, HttpMethod.Post, HttpMethod.Post,
                HttpMethod.Get, HttpMethod.Post],
            requests.Select(static item => item.Method)
        );
        Assert.Equal(
            "/v1beta/models/gemini%2Fexact",
            requests[0].Uri.PathAndQuery
        );
        Assert.DoesNotContain(
            "gemini-key",
            requests[0].Uri.ToString(),
            StringComparison.Ordinal
        );
        Assert.Equal(
            "gemini-key",
            Assert.Single(requests[0].Headers["x-goog-api-key"])
        );
        Assert.All(
            requests.Where(static item => item.Method == HttpMethod.Post),
            item => {
                using JsonDocument document = JsonDocument.Parse(item.Body!);
                Assert.Equal(
                    65_536,
                    document.RootElement.GetProperty("generationConfig")
                        .GetProperty("maxOutputTokens").GetInt32()
                );
            }
        );
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"outputTokenLimit\":null}")]
    [InlineData("{\"outputTokenLimit\":-1}")]
    [InlineData("{\"outputTokenLimit\":1e3}")]
    [InlineData("{\"OutputTokenLimit\":1}")]
    [InlineData("{\"outputTokenLimit\":1,\"outputTokenLimit\":2}")]
    public async Task Gemini_RejectsMalformedCapabilityBeforePost(
        string body
    ) {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(body))
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        InvalidDataException exception = await Assert.ThrowsAsync<
            InvalidDataException
        >(() => client.StreamCompletionAsync(
            Request("gemini-bad"),
            null,
            CancellationToken.None
        ));

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.DoesNotContain(body, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gemini_StatusAndOversizeDoNotLeakOrPoisonCache() {
        const string KeyCanary = "GEMINI_KEY_CANARY";
        const string BodyCanary = "GEMINI_BODY_CANARY";
        var getCount = 0;
        var handler = new RecordingHandler((request, _) => {
            HttpResponseMessage response = request.Method == HttpMethod.Post
                ? GeminiCompletionResponse()
                : Interlocked.Increment(ref getCount) switch {
                1 => new HttpResponseMessage(HttpStatusCode.BadGateway) {
                    Content = new StringContent(BodyCanary)
                },
                2 => OversizeStreamingResponse(),
                _ => JsonResponse("{\"outputTokenLimit\":65536}")
            };
            return Task.FromResult(response);
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(KeyCanary, httpClient);

        Exception status = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.StreamCompletionAsync(
                Request("gemini-retry"), null, CancellationToken.None
            )
        );
        Exception oversize = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.StreamCompletionAsync(
                Request("gemini-retry"), null, CancellationToken.None
            )
        );
        _ = await client.StreamCompletionAsync(
            Request("gemini-retry"), null, CancellationToken.None
        );

        string failures = status + oversize.ToString();
        Assert.DoesNotContain(KeyCanary, failures, StringComparison.Ordinal);
        Assert.DoesNotContain(BodyCanary, failures, StringComparison.Ordinal);
        Assert.Equal(3, getCount);
        Assert.Equal(HttpMethod.Post, handler.Requests.Last().Method);
    }

    [Fact]
    public async Task Gemini_ConcurrentCapabilityFetchIsOneFlight() {
        var fetchStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFetch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var getCount = 0;
        var handler = new RecordingHandler(async (request, token) => {
            if (request.Method == HttpMethod.Post) {
                return GeminiCompletionResponse();
            }
            Interlocked.Increment(ref getCount);
            fetchStarted.TrySetResult();
            await releaseFetch.Task.WaitAsync(token);
            return JsonResponse("{\"outputTokenLimit\":65536}");
        });
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        Task<CompletionResult>[] calls = Enumerable.Range(0, 8)
            .Select(_ => client.StreamCompletionAsync(
                Request("gemini-concurrent"),
                null,
                CancellationToken.None
            ))
            .ToArray();
        await fetchStarted.Task;
        Assert.Equal(1, getCount);
        releaseFetch.TrySetResult();
        await Task.WhenAll(calls);
        Assert.Equal(1, getCount);
    }

    private static CompletionRequest Request(string modelId) => new(
        modelId,
        new CompletionPromptPrefix(
            "system",
            CompletionOutputContract.ProviderDefault(
                ImmutableArray<ToolDefinition>.Empty
            ),
            [new ObservationMessage("hello")]
        ),
        []
    );

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://provider.example/") };

    private static HttpResponseMessage JsonResponse(string body) => new(
        HttpStatusCode.OK
    ) {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage OversizeStreamingResponse() {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(
            ProviderModelCapabilityResponse.MaximumResponseBytes + 1
        );
        bytes.AsSpan().Fill((byte)'x');
        var response = new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StreamContent(new MemoryStream(bytes))
        };
        response.Content.Headers.ContentType = new(
            "application/json"
        );
        return response;
    }

    private static HttpResponseMessage AnthropicCompletionResponse() => new(
        HttpStatusCode.OK
    ) {
        Content = new StringContent(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{}}\n\n"
                + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n"
                + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
            Encoding.UTF8,
            "text/event-stream"
        )
    };

    private static HttpResponseMessage GeminiCompletionResponse() => new(
        HttpStatusCode.OK
    ) {
        Content = new StringContent(
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n",
            Encoding.UTF8,
            "text/event-stream"
        )
    };

    private sealed class FixedClientFactory(ICompletionClient client) : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection) => client;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            responder
    ) : HttpMessageHandler {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>
        > _responder = responder;

        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase
                ),
                body
            ));
            return await _responder(request, cancellationToken);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string[]> Headers,
        string? Body
    );
}
