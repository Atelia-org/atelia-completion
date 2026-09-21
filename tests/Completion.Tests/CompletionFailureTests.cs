using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.Gemini;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class CompletionFailureTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongErrorBody_DiagnosticsAreBoundedWithoutLosingFailureFacts(bool includeDiagnosticBody) {
        string body = "{\n\"padding\":\"" + new string('x', 20_000)
            + "\",\"error\":{\"code\":\"insufficient_quota\",\"message\":\"TRAILING_PRIVATE_TEXT\"}}";
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(body) };
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(123));
        var exception = await CompletionHttpRequestUtility.CreateHttpFailureAsync(response, "fixture request", default, includeDiagnosticBody);
        Assert.Equal(new(CompletionFailureKind.Http, 429, "insufficient_quota", TimeSpan.FromSeconds(123)), exception.Failure);
        const string prefix = "fixture request failed with HTTP status 429.";
        Assert.Equal(includeDiagnosticBody ? prefix + " Response body: " + body.ReplaceLineEndings(" ").Trim()[..512] : prefix,
            exception.Message);
        Assert.DoesNotContain("TRAILING_PRIVATE_TEXT", exception.ToString());
        Assert.DoesNotContain("insufficient_quota", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task TransportCancellationWithoutCallerCancellation_IsClassified() {
        using var http = new HttpClient(new Handler(_ => throw new TaskCanceledException("transport timeout"))) {
            BaseAddress = new Uri("https://example.invalid/")
        };
        var exception = await Assert.ThrowsAsync<CompletionFailureException>(() =>
            new OpenAIChatClient(null, http).StreamCompletionAsync(Request(), null));
        Assert.Equal(CompletionFailureKind.Transport, exception.Failure.Kind);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Theory]
    [InlineData(0, "data: {\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"busy\"}}\n\n", "rate_limit_exceeded")]
    [InlineData(1, "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"server_error\",\"message\":\"busy\"}}}\n\n", "server_error")]
    [InlineData(1, "data: {\"type\":\"error\",\"code\":\"rate_limit_exceeded\",\"message\":\"busy\"}\n\n", "rate_limit_exceeded")]
    [InlineData(2, "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"busy\"}}\n\n", "overloaded_error")]
    [InlineData(3, "data: {\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\",\"message\":\"busy\"}}\n\n", "UNAVAILABLE")]
    public async Task ProviderFailure_PreservesStableCode(int surface, string payload, string expectedCode) {
        using var http = new HttpClient(new Handler(request => request.Method == HttpMethod.Get
            ? new(HttpStatusCode.OK) { Content = new StringContent("{\"outputTokenLimit\":8192,\"max_tokens\":8192}") }
            : Sse(new MemoryStream(Encoding.UTF8.GetBytes(payload))))) { BaseAddress = new Uri("https://example.invalid/") };
        ICompletionClient client = surface switch {
            0 => new OpenAIChatClient(null, http),
            1 => new OpenAIResponsesClient(null, http),
            2 => new AnthropicClient(null, http),
            _ => new GeminiClient(null, http)
        };
        CompletionResult result = await client.StreamCompletionAsync(Request(), null);
        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal(new(CompletionFailureKind.Provider, ProviderCode: expectedCode), result.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelCapabilitySendFailure_IsClassifiedBeforePost(bool gemini) {
        var expected = new HttpRequestException("preflight failed");
        using var http = new HttpClient(new Handler(request => {
            Assert.Equal(HttpMethod.Get, request.Method);
            throw expected;
        })) { BaseAddress = new Uri("https://example.invalid/") };
        ICompletionClient client = gemini ? new GeminiClient(null, http) : new AnthropicClient(null, http);
        var exception = await Assert.ThrowsAsync<CompletionFailureException>(() => client.StreamCompletionAsync(Request(), null));
        Assert.Equal(CompletionFailureKind.Transport, exception.Failure.Kind);
        Assert.Same(expected, exception.InnerException);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task HttpFailure_PreservesFactsAndDisposesResponse(int status) {
        var content = new TrackingContent("{\"error\":{\"code\":\"insufficient_quota\"}}");
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(123));
        using var http = new HttpClient(new Handler(_ => response));
        var exception = await Assert.ThrowsAsync<CompletionFailureException>(() =>
            CompletionHttpRequestUtility.SendStreamingRequestAsync(http,
                new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/"), "test", default));
        Assert.Equal(new(CompletionFailureKind.Http, status, "insufficient_quota", TimeSpan.FromSeconds(123)), exception.Failure);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task ErrorBodyReadFailure_PreservesHttpFactsAndDisposesResponse() {
        var content = new TrackingContent(null);
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content };
        using var http = new HttpClient(new Handler(_ => response));
        var exception = await Assert.ThrowsAsync<CompletionFailureException>(() =>
            CompletionHttpRequestUtility.SendStreamingRequestAsync(http,
                new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/"), "test", default));
        Assert.Equal(503, exception.Failure.HttpStatusCode);
        Assert.NotNull(exception.InnerException);
        Assert.True(content.Disposed);
    }

    [Fact]
    public void RetryAfter_DateAndInvalidValues() {
        using var response = new HttpResponseMessage();
        var now = DateTimeOffset.Parse("2026-09-16T00:00:00Z");
        response.Headers.RetryAfter = new(now.AddSeconds(901));
        Assert.Equal(TimeSpan.FromSeconds(901), CompletionHttpRequestUtility.ReadRetryAfter(response, now));
        response.Headers.RetryAfter = new(now.AddSeconds(-1));
        Assert.Null(CompletionHttpRequestUtility.ReadRetryAfter(response, now));
        response.Headers.Remove("Retry-After");
        response.Headers.TryAddWithoutValidation("Retry-After", "not-a-delay");
        Assert.Null(CompletionHttpRequestUtility.ReadRetryAfter(response, now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_ReturnsWithoutReadingTrailingUsage(bool gemini) {
        string terminal = gemini
            ? "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n"
            : "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n";
        var stream = new FailAfterPayloadStream(terminal);
        using var http = new HttpClient(new Handler(request => request.Method == HttpMethod.Get
            ? new(HttpStatusCode.OK) { Content = new StringContent("{\"outputTokenLimit\":8192}") }
            : Sse(stream))) { BaseAddress = new Uri("https://example.invalid/") };
        ICompletionClient client = gemini
            ? new GeminiClient(null, http)
            : new OpenAIChatClient(
                null,
                http,
                OpenAIChatDialects.SgLangCompatible
            );
        CompletionResult result = await client.StreamCompletionAsync(Request(), null);
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        Assert.Equal("ok", result.Message.GetFlattenedText());
        Assert.Null(result.Usage.OutputTokens);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ObserverIOException_IsNotTransportFailure() {
        using var http = new HttpClient(new Handler(_ => Sse(new MemoryStream(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"))))) {
            BaseAddress = new Uri("https://example.invalid/")
        };
        var observer = new CompletionStreamObserver();
        var expected = new IOException("observer failure");
        observer.ReceivedTextDelta += _ => throw expected;
        var actual = await Assert.ThrowsAsync<IOException>(() => new OpenAIChatClient(null, http).StreamCompletionAsync(Request(), observer));
        Assert.Same(expected, actual);
    }

    private static CompletionRequest Request() => new("test",
        new CompletionPromptPrefix("", CompletionOutputContract.ProviderDefault([]), [new ObservationMessage("test")]), []);

    private static HttpResponseMessage Sse(Stream stream) {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class TrackingContent(string? text) : HttpContent {
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => text is null
            ? Task.FromException(new IOException("body read failed"))
            : stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class FailAfterPayloadStream(string payload) : MemoryStream(Encoding.UTF8.GetBytes(payload)) {
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == Length ? ValueTask.FromException<int>(new IOException("must not read after terminal")) : base.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
