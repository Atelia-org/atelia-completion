using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.Completion.Transport.Tests;

public sealed class CompletionHttpTransportTests {
    private const string LocalLlmE2EEnvVar = "ATELIA_RUN_LOCAL_LLM_E2E";
    private static readonly Uri LocalLlmBaseAddress = new("http://localhost:8000/");

    [Fact]
    public async Task CapturePipeline_RecordsRequestAndStreamingResponseText_ForOpenAIClient() {
        var captureSink = new InMemoryCompletionHttpExchangeSink();
        using var httpClient = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(
                new StubHttpMessageHandler(
                    new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(
                            """
                            data: {"choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}],"usage":null}

                            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}

                            data: [DONE]

                            """,
                            Encoding.UTF8,
                            "text/event-stream"
                        )
                    }
                )
            )
            .AddExchangeSink(captureSink)
            .Build();

        httpClient.BaseAddress = new Uri("http://localhost:8000/");

        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.SgLangCompatible);

        var result = await client.StreamCompletionAsync(CreateRequest("gpt-4.1"), null, CancellationToken.None);

        Assert.Equal("hello", result.Message.GetFlattenedText());

        var exchange = Assert.Single(captureSink.GetSnapshot());
        Assert.Equal("POST", exchange.Method);
        Assert.Equal("http://localhost:8000/v1/chat/completions", exchange.RequestUri);
        Assert.Contains("\"model\":\"gpt-4.1\"", exchange.RequestText, StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", exchange.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayPipeline_CanReplaceRemoteServer_ForAnthropicClient() {
        var captureSink = new InMemoryCompletionHttpExchangeSink();
        using var httpClient = new CompletionHttpClientBuilder()
            .UseReplayResponder(new AnthropicReplayResponder())
            .AddExchangeSink(captureSink)
            .Build();

        httpClient.BaseAddress = new Uri("http://localhost:8000/");

        var client = new AnthropicClient(apiKey: null, httpClient: httpClient);
        var result = await client.StreamCompletionAsync(CreateRequest("claude-3-5-sonnet-20241022"), null, CancellationToken.None);

        Assert.Equal("world", result.Message.GetFlattenedText());

        var exchange = Assert.Single(captureSink.GetSnapshot());
        Assert.Equal("POST", exchange.Method);
        Assert.Equal("http://localhost:8000/v1/messages", exchange.RequestUri);
        Assert.Contains("claude-3-5-sonnet-20241022", exchange.RequestText, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"world\"", exchange.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonLinesGoldenLogSink_AppendsCamelCaseExchangeEntries() {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "atelia-completion-tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDirectory, "golden-log.jsonl");

        try {
            using var httpClient = new CompletionHttpClientBuilder()
                .UsePrimaryHandler(
                    new StubHttpMessageHandler(
                        new HttpResponseMessage(HttpStatusCode.OK) {
                            Content = new StringContent(
                                """
                                data: {"choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}],"usage":null}

                                data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}

                                data: [DONE]

                                """,
                                Encoding.UTF8,
                                "text/event-stream"
                            )
                        }
                    )
                )
                .AddJsonLinesGoldenLogSink(filePath)
                .Build();

            httpClient.BaseAddress = new Uri("http://localhost:8000/");

            var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.SgLangCompatible);
            var result = await client.StreamCompletionAsync(CreateRequest("gpt-4.1"), null, CancellationToken.None);

            Assert.Equal("hello", result.Message.GetFlattenedText());
            Assert.True(File.Exists(filePath));

            var lines = await File.ReadAllLinesAsync(filePath, CancellationToken.None);
            var line = Assert.Single(lines);
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.Equal("POST", root.GetProperty("method").GetString());
            Assert.Equal("http://localhost:8000/v1/chat/completions", root.GetProperty("requestUri").GetString());
            Assert.Contains("gpt-4.1", root.GetProperty("requestText").GetString(), StringComparison.Ordinal);
            Assert.Contains("data: [DONE]", root.GetProperty("responseText").GetString(), StringComparison.Ordinal);
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JsonLinesReplayResponder_ReplaysRecordedOpenAiExchangeInSequence() {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "atelia-completion-tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDirectory, "openai-replay.jsonl");

        try {
            using (var recordingHttpClient = new CompletionHttpClientBuilder()
                .UsePrimaryHandler(
                    new StubHttpMessageHandler(
                        new HttpResponseMessage(HttpStatusCode.OK) {
                            Content = new StringContent(
                                """
                                data: {"choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}],"usage":null}

                                data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}

                                data: [DONE]

                                """,
                                Encoding.UTF8,
                                "text/event-stream"
                            )
                        }
                    )
                )
                .AddJsonLinesGoldenLogSink(filePath)
                .Build()) {
                recordingHttpClient.BaseAddress = new Uri("http://localhost:8000/");

                var recordingClient = new OpenAIChatClient(
                    apiKey: null,
                    httpClient: recordingHttpClient,
                    dialect: OpenAIChatDialects.SgLangCompatible
                );

                var recorded = await recordingClient.StreamCompletionAsync(CreateRequest("gpt-4.1"), null, CancellationToken.None);
                Assert.Equal("hello", recorded.Message.GetFlattenedText());
            }

            using var replayHttpClient = new CompletionHttpClientBuilder()
                .UseJsonLinesReplayResponder(filePath)
                .Build();

            replayHttpClient.BaseAddress = new Uri("http://localhost:8000/");

            var replayClient = new OpenAIChatClient(
                apiKey: null,
                httpClient: replayHttpClient,
                dialect: OpenAIChatDialects.SgLangCompatible
            );

            var replayed = await replayClient.StreamCompletionAsync(CreateRequest("gpt-4.1"), null, CancellationToken.None);
            Assert.Equal("hello", replayed.Message.GetFlattenedText());
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JsonLinesReplayResponder_ThrowsWhenActualRequestDriftsFromGoldenLog() {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "atelia-completion-tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDirectory, "mismatch.jsonl");

        try {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(
                filePath,
                "{" +
                "\"method\":\"POST\"," +
                "\"requestUri\":\"http://localhost:8000/v1/chat/completions\"," +
                "\"requestText\":\"expected-request\"," +
                "\"statusCode\":200," +
                "\"responseText\":\"data: [DONE]\\n\"" +
                "}\n"
            );

            var responder = new JsonLinesCompletionHttpReplayResponder(filePath);
            var exception = Assert.Throws<InvalidOperationException>(
                () => responder.CreateResponse(
                    new CompletionHttpReplayRequest(
                        Method: "POST",
                        RequestUri: "http://localhost:8000/v1/chat/completions",
                        RequestText: "different-request"
                    )
                )
            );

            Assert.Contains("Replay request text mismatch", exception.Message, StringComparison.Ordinal);

            using var response = responder.CreateResponse(
                new CompletionHttpReplayRequest(
                    Method: "POST",
                    RequestUri: "http://localhost:8000/v1/chat/completions",
                    RequestText: "expected-request"
                )
            );
            var responseText = response.Content is null
                ? null
                : await response.Content.ReadAsStringAsync(CancellationToken.None);
            Assert.Contains("[DONE]", responseText, StringComparison.Ordinal);
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TransportFactory_CreateJsonLinesReplayClient_ReplaysWithoutExplicitBuilderCalls() {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "atelia-completion-tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDirectory, "factory-replay.jsonl");

        try {
            using (var recordingHttpClient = new CompletionHttpClientBuilder()
                .UsePrimaryHandler(
                    new StubHttpMessageHandler(
                        new HttpResponseMessage(HttpStatusCode.OK) {
                            Content = new StringContent(
                                """
                                data: {"choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}],"usage":null}

                                data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}

                                data: [DONE]

                                """,
                                Encoding.UTF8,
                                "text/event-stream"
                            )
                        }
                    )
                )
                .AddJsonLinesGoldenLogSink(filePath)
                .Build()) {
                recordingHttpClient.BaseAddress = new Uri("http://localhost:8000/");

                var recordingClient = new OpenAIChatClient(
                    apiKey: null,
                    httpClient: recordingHttpClient,
                    dialect: OpenAIChatDialects.SgLangCompatible
                );

                var recorded = await recordingClient.StreamCompletionAsync(CreateRequest("gpt-4.1"), null, CancellationToken.None);
                Assert.Equal("hello", recorded.Message.GetFlattenedText());
            }

            using var httpClient = CompletionHttpTransportFactory.CreateJsonLinesReplayClient(
                new Uri("http://localhost:8000/"),
                filePath
            );
            var client = new OpenAIChatClient(
                apiKey: null,
                httpClient: httpClient,
                dialect: OpenAIChatDialects.SgLangCompatible
            );

            var result = await client.StreamCompletionAsync(CreateRequest("gpt-4.1"), null, CancellationToken.None);
            Assert.Equal("hello", result.Message.GetFlattenedText());
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void TransportFactory_CreateFromPaths_DescribesRecordMode() {
        var setup = CompletionHttpTransportFactory.CreateFromPaths(
            new Uri("http://localhost:8000/"),
            recordLogPath: "golden.jsonl",
            replayLogPath: null
        );

        using var httpClient = setup.HttpClient;
        Assert.Equal(CompletionHttpTransportMode.Record, setup.Mode);
        Assert.Equal("golden.jsonl", setup.ArtifactPath);
        Assert.Contains("record -> golden.jsonl", setup.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturePipeline_RecordsTransportFailureWithoutFakingResponseFields() {
        var captureSink = new InMemoryCompletionHttpExchangeSink();
        using var httpClient = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(new ThrowingHttpMessageHandler(new HttpRequestException("simulated connect failure")))
            .AddExchangeSink(captureSink)
            .Build();

        httpClient.BaseAddress = new Uri("http://localhost:8000/");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => httpClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") {
                    Content = new StringContent("{\"model\":\"gpt-4.1\"}", Encoding.UTF8, "application/json")
                },
                CancellationToken.None
            )
        );

        Assert.Contains("simulated connect failure", exception.Message, StringComparison.Ordinal);

        var exchange = Assert.Single(captureSink.GetSnapshot());
        Assert.Equal("POST", exchange.Method);
        Assert.Equal("http://localhost:8000/v1/chat/completions", exchange.RequestUri);
        Assert.Contains("\"model\":\"gpt-4.1\"", exchange.RequestText, StringComparison.Ordinal);
        Assert.Null(exchange.StatusCode);
        Assert.Null(exchange.ResponseText);
        Assert.Equal("System.Net.Http.HttpRequestException: simulated connect failure", exchange.ErrorText);
    }

    [Fact]
    public void JsonLinesReplayResponder_ReplaysRecordedTransportFailureAsHttpRequestException() {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "atelia-completion-tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDirectory, "transport-failure.jsonl");

        try {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(
                filePath,
                "{" +
                "\"method\":\"POST\"," +
                "\"requestUri\":\"http://localhost:8000/v1/chat/completions\"," +
                "\"requestText\":\"expected-request\"," +
                "\"errorText\":\"System.Net.Http.HttpRequestException: simulated connect failure\"" +
                "}\n"
            );

            var responder = new JsonLinesCompletionHttpReplayResponder(filePath);
            var exception = Assert.Throws<HttpRequestException>(
                () => responder.CreateResponse(
                    new CompletionHttpReplayRequest(
                        Method: "POST",
                        RequestUri: "http://localhost:8000/v1/chat/completions",
                        RequestText: "expected-request"
                    )
                )
            );

            Assert.Contains("simulated connect failure", exception.Message, StringComparison.Ordinal);
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "LocalE2E")]
    public async Task LocalRoundTripE2E_OpenAI_RecordThenReplayAgainstLocalEndpoint() {
        if (!ShouldRunLocalLlmE2E()) {
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "atelia-completion-tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDirectory, "openai-local-roundtrip.jsonl");

        try {
            var liveResult = await RecordAndReplayOpenAiAsync(filePath, modelId: "ignored-local-openai-model");
            Assert.False(string.IsNullOrWhiteSpace(liveResult.LiveText));
            Assert.Equal(liveResult.LiveText, liveResult.ReplayedText);

            var lines = await File.ReadAllLinesAsync(filePath, CancellationToken.None);
            var line = Assert.Single(lines);
            using var document = JsonDocument.Parse(line);
            Assert.Equal("http://localhost:8000/v1/chat/completions", document.RootElement.GetProperty("requestUri").GetString());
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "LocalE2E")]
    public async Task LocalRoundTripE2E_Anthropic_RecordThenReplayAgainstLocalEndpoint() {
        if (!ShouldRunLocalLlmE2E()) {
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "atelia-completion-tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDirectory, "anthropic-local-roundtrip.jsonl");

        try {
            var liveResult = await RecordAndReplayAnthropicAsync(filePath, modelId: "ignored-local-anthropic-model");
            Assert.False(string.IsNullOrWhiteSpace(liveResult.LiveText));
            Assert.Equal(liveResult.LiveText, liveResult.ReplayedText);

            var lines = await File.ReadAllLinesAsync(filePath, CancellationToken.None);
            var line = Assert.Single(lines);
            using var document = JsonDocument.Parse(line);
            Assert.Equal("http://localhost:8000/v1/messages", document.RootElement.GetProperty("requestUri").GetString());
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static CompletionRequest CreateRequest(string modelId) {
        return new CompletionRequest(
            ModelId: modelId,
            SystemPrompt: "system",
            Context: new[] { new ObservationMessage("hello") },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );
    }

    private static bool ShouldRunLocalLlmE2E() {
        var value = Environment.GetEnvironmentVariable(LocalLlmE2EEnvVar);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(string LiveText, string ReplayedText)> RecordAndReplayOpenAiAsync(string filePath, string modelId) {
        var recordSetup = CompletionHttpTransportFactory.CreateFromPaths(
            LocalLlmBaseAddress,
            recordLogPath: filePath,
            replayLogPath: null
        );

        string liveText;
        using (var recordClient = recordSetup.HttpClient) {
            var openAiClient = new OpenAIChatClient(
                apiKey: "ignored-local-api-key",
                httpClient: recordClient,
                dialect: OpenAIChatDialects.SgLangCompatible,
                options: OpenAIChatClientOptions.QwenThinkingDisabled()
            );

            var live = await openAiClient.StreamCompletionAsync(CreateRequest(modelId), null, CancellationToken.None);
            liveText = live.Message.GetFlattenedText();
        }

        string replayedText;
        using (var replayClient = CompletionHttpTransportFactory.CreateJsonLinesReplayClient(LocalLlmBaseAddress, filePath)) {
            var replayOpenAiClient = new OpenAIChatClient(
                apiKey: "ignored-local-api-key",
                httpClient: replayClient,
                dialect: OpenAIChatDialects.SgLangCompatible,
                options: OpenAIChatClientOptions.QwenThinkingDisabled()
            );

            var replayed = await replayOpenAiClient.StreamCompletionAsync(CreateRequest(modelId), null, CancellationToken.None);
            replayedText = replayed.Message.GetFlattenedText();
        }

        return (liveText, replayedText);
    }

    private static async Task<(string LiveText, string ReplayedText)> RecordAndReplayAnthropicAsync(string filePath, string modelId) {
        var recordSetup = CompletionHttpTransportFactory.CreateFromPaths(
            LocalLlmBaseAddress,
            recordLogPath: filePath,
            replayLogPath: null
        );

        string liveText;
        using (var recordClient = recordSetup.HttpClient) {
            var anthropicClient = new AnthropicClient(
                apiKey: "ignored-local-api-key",
                httpClient: recordClient,
                baseAddress: null
            );

            var live = await anthropicClient.StreamCompletionAsync(CreateRequest(modelId), null, CancellationToken.None);
            liveText = live.Message.GetFlattenedText();
        }

        string replayedText;
        using (var replayClient = CompletionHttpTransportFactory.CreateJsonLinesReplayClient(LocalLlmBaseAddress, filePath)) {
            var replayAnthropicClient = new AnthropicClient(
                apiKey: "ignored-local-api-key",
                httpClient: replayClient,
                baseAddress: null
            );

            var replayed = await replayAnthropicClient.StreamCompletionAsync(CreateRequest(modelId), null, CancellationToken.None);
            replayedText = replayed.Message.GetFlattenedText();
        }

        return (liveText, replayedText);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response) {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return Task.FromResult(_response);
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler {
        private readonly Exception _exception;

        public ThrowingHttpMessageHandler(Exception exception) {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return Task.FromException<HttpResponseMessage>(_exception);
        }
    }

    private sealed class AnthropicReplayResponder : ICompletionHttpReplayResponder {
        public HttpResponseMessage CreateResponse(CompletionHttpReplayRequest request) {
            Assert.Equal("POST", request.Method);
            Assert.Equal("http://localhost:8000/v1/messages", request.RequestUri);
            Assert.Contains("\"model\":\"claude-3-5-sonnet-20241022\"", request.RequestText, StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-3-5-sonnet-20241022","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

                    data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                    data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"world"}}

                    data: {"type":"content_block_stop","index":0}

                    data: {"type":"message_stop"}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            };
        }
    }
}
