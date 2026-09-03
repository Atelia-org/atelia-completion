using System.Collections.Immutable;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.OpenAI;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Atelia.Completion.Transport.Tests;

public sealed class CompletionHttpTransportTests {
    private const string LocalLlmE2EEnvVar = "ATELIA_RUN_LOCAL_LLM_E2E";
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private static readonly Uri LocalLlmBaseAddress = new("http://localhost:8000/");

    [Fact]
    public void CreateLiveClient_NormalizesBaseAddressWithPathPrefix() {
        using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(new Uri("http://localhost:8000/provider"));

        Assert.Equal(new Uri("http://localhost:8000/provider/"), httpClient.BaseAddress);
        Assert.Equal(Timeout.InfiniteTimeSpan, httpClient.Timeout);
    }

    [Fact]
    public void BuildDirectly_UsesInfiniteTimeoutForPrimaryAndReplayPipelines() {
        using var primaryClient = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(
                new StubHttpMessageHandler(
                    new HttpResponseMessage(HttpStatusCode.OK)
                )
            )
            .Build();
        using var replayClient = new CompletionHttpClientBuilder()
            .UseReplayResponder(new AnthropicReplayResponder())
            .Build();

        Assert.Equal(Timeout.InfiniteTimeSpan, primaryClient.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, replayClient.Timeout);
    }

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
    public async Task CapturePipeline_ResponseEof_IgnoresThrowingSinkAndContinuesOtherSinks() {
        var captureSink = new InMemoryCompletionHttpExchangeSink();
        using var httpClient = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(
                new StubHttpMessageHandler(
                    new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(
                            "provider-response",
                            Encoding.UTF8,
                            "text/plain"
                        )
                    }
                )
            )
            .AddExchangeSink(
                new ThrowingCompletionHttpExchangeSink(
                    new OperationCanceledException("sink-only cancellation")
                )
            )
            .AddExchangeSink(captureSink)
            .Build();
        httpClient.BaseAddress = new Uri("http://localhost:8000/");

        using HttpResponseMessage response = await httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "response"),
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None
        );
        string responseText = await response.Content.ReadAsStringAsync(
            CancellationToken.None
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("provider-response", responseText);
        CompletionHttpExchange exchange = Assert.Single(
            captureSink.GetSnapshot()
        );
        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal("provider-response", exchange.ResponseText);
    }

    [Fact]
    public async Task CapturePipeline_ResponseDispose_IgnoresThrowingSink() {
        var captureSink = new InMemoryCompletionHttpExchangeSink();
        using var httpClient = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(
                new StubHttpMessageHandler(
                    new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(
                            "unread-provider-response",
                            Encoding.UTF8,
                            "text/plain"
                        )
                    }
                )
            )
            .AddExchangeSink(
                new ThrowingCompletionHttpExchangeSink(
                    new InvalidOperationException("sink-only failure")
                )
            )
            .AddExchangeSink(captureSink)
            .Build();
        httpClient.BaseAddress = new Uri("http://localhost:8000/");

        HttpResponseMessage response = await httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "response"),
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None
        );

        Exception? disposeFailure = Record.Exception(response.Dispose);

        Assert.Null(disposeFailure);
        CompletionHttpExchange exchange = Assert.Single(
            captureSink.GetSnapshot()
        );
        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal(string.Empty, exchange.ResponseText);
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

        CompletionHttpExchange[] exchanges = captureSink.GetSnapshot().ToArray();
        Assert.Equal(2, exchanges.Length);
        Assert.Equal("GET", exchanges[0].Method);
        Assert.Equal(
            "http://localhost:8000/v1/models/claude-3-5-sonnet-20241022",
            exchanges[0].RequestUri
        );
        Assert.Equal("POST", exchanges[1].Method);
        Assert.Equal("http://localhost:8000/v1/messages", exchanges[1].RequestUri);
        Assert.Contains("claude-3-5-sonnet-20241022", exchanges[1].RequestText, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"world\"", exchanges[1].ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonLinesGoldenLogSink_AppendsCamelCaseExchangeEntries() {
        if (!SupportsHardenedRawSink()) { return; }

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
            if (!OperatingSystem.IsWindows()) {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(filePath)
                );
            }

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
    [SupportedOSPlatform("linux")]
    public void JsonLinesGoldenLogSink_TightensExistingOwnedFileAndAppends() {
        if (!SupportsHardenedRawSink()) { return; }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "atelia-completion-tests",
            Guid.NewGuid().ToString("N")
        );
        var filePath = Path.Combine(tempDirectory, "raw.jsonl");
        try {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(filePath, "preexisting\n");
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead
            );
            var sink = new JsonLinesCompletionHttpExchangeFileSink(filePath);

            sink.OnExchange(new CompletionHttpExchange(
                "POST",
                "https://example.invalid/",
                "prompt",
                400,
                "response",
                null
            ));

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(filePath)
            );
            string text = File.ReadAllText(filePath);
            Assert.StartsWith("preexisting\n", text, StringComparison.Ordinal);
            Assert.Contains("\"requestText\":\"prompt\"", text, StringComparison.Ordinal);
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void JsonLinesGoldenLogSink_RejectsUnixSymbolicLink() {
        if (!SupportsHardenedRawSink()) { return; }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "atelia-completion-tests",
            Guid.NewGuid().ToString("N")
        );
        var targetPath = Path.Combine(tempDirectory, "target.jsonl");
        var linkPath = Path.Combine(tempDirectory, "raw.jsonl");
        try {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(targetPath, string.Empty);
            File.SetUnixFileMode(
                targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );
            File.CreateSymbolicLink(linkPath, targetPath);
            var sink = new JsonLinesCompletionHttpExchangeFileSink(linkPath);

            InvalidOperationException exception = Assert.Throws<
                InvalidOperationException
            >(() => sink.OnExchange(new CompletionHttpExchange(
                "POST",
                "https://example.invalid/",
                "prompt",
                400,
                "response",
                null
            )));

            Assert.Contains(
                "symbolic link",
                exception.Message,
                StringComparison.Ordinal
            );
            Assert.Equal(string.Empty, File.ReadAllText(targetPath));
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void JsonLinesGoldenLogSink_RejectsUnixFifoWithoutWriting() {
        if (!SupportsHardenedRawSink()) { return; }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "atelia-completion-tests",
            Guid.NewGuid().ToString("N")
        );
        var fifoPath = Path.Combine(tempDirectory, "raw.jsonl");
        try {
            Directory.CreateDirectory(tempDirectory);
            Assert.Equal(0, MakeFifo(fifoPath, 0x180));

            // Keep a non-blocking reader open so even a regressed writer-side
            // open cannot hang this test before the sink validates file type.
            int readerDescriptor = OpenForRead(
                fifoPath,
                OpenReadOnly | OpenNonBlocking
            );
            Assert.True(
                readerDescriptor >= 0,
                $"Failed to open FIFO reader (errno {Marshal.GetLastPInvokeError()})."
            );
            using var readerHandle = new SafeFileHandle(
                new IntPtr(readerDescriptor),
                ownsHandle: true
            );
            var sink = new JsonLinesCompletionHttpExchangeFileSink(fifoPath);

            InvalidOperationException exception = Assert.Throws<
                InvalidOperationException
            >(() => sink.OnExchange(new CompletionHttpExchange(
                "POST",
                "https://example.invalid/",
                "prompt",
                400,
                "response",
                null
            )));

            Assert.Contains(
                "regular file",
                exception.Message,
                StringComparison.Ordinal
            );
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void JsonLinesGoldenLogSink_KeepsValidatedHandleWhenPathIsReplaced() {
        if (!SupportsHardenedRawSink()) { return; }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "atelia-completion-tests",
            Guid.NewGuid().ToString("N")
        );
        var filePath = Path.Combine(tempDirectory, "raw.jsonl");
        var openedPath = Path.Combine(tempDirectory, "opened.jsonl");
        var victimPath = Path.Combine(tempDirectory, "victim.txt");
        try {
            Directory.CreateDirectory(tempDirectory);
            File.WriteAllText(victimPath, "victim");
            File.SetUnixFileMode(
                victimPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );
            var sink = new JsonLinesCompletionHttpExchangeFileSink(
                filePath,
                afterLinuxHandleValidatedForTest: () => {
                    File.Move(filePath, openedPath);
                    File.CreateSymbolicLink(filePath, victimPath);
                }
            );

            sink.OnExchange(new CompletionHttpExchange(
                "POST",
                "https://example.invalid/",
                "fixed-handle-prompt",
                200,
                "response",
                null
            ));

            Assert.Contains(
                "fixed-handle-prompt",
                File.ReadAllText(openedPath),
                StringComparison.Ordinal
            );
            Assert.Equal("victim", File.ReadAllText(victimPath));
        }
        finally {
            if (Directory.Exists(tempDirectory)) {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JsonLinesReplayResponder_ReplaysRecordedOpenAiExchangeInSequence() {
        if (!SupportsHardenedRawSink()) { return; }

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
    public async Task JsonLinesGoldenLogSink_RecordsAndReplaysGeminiExchange() {
        if (!SupportsHardenedRawSink() || !GeminiProductionTypesPresent()) {
            return;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "atelia-completion-tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(tempDirectory, "gemini-replay.jsonl");

        try {
            using (var recordingHttpClient = new CompletionHttpClientBuilder()
                .UsePrimaryHandler(
                    new CapabilityAwareStubHttpMessageHandler(
                        "{\"outputTokenLimit\":65536}",
                        new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(
                            """
                                data: {"candidates":[{"content":{"role":"model","parts":[{"text":"hello"}]}}]}

                                data: {"candidates":[{"content":{"role":"model","parts":[{"text":" world"}]},"finishReason":"STOP"}]}

                                """
                                + "\n",
                            Encoding.UTF8,
                            "text/event-stream"
                        )
                    }
                )
            )
                .AddJsonLinesGoldenLogSink(filePath)
                .Build()) {
                recordingHttpClient.BaseAddress = new Uri("http://localhost:8000/");

                var recorded = await InvokeGeminiCompletionAsync(recordingHttpClient, CreateRequest("gemini-2.5-flash"));
                Assert.Equal("hello world", recorded.Message.GetFlattenedText());
            }

            var lines = await File.ReadAllLinesAsync(filePath, CancellationToken.None);
            Assert.Equal(2, lines.Length);
            using (var document = JsonDocument.Parse(lines[1])) {
                var root = document.RootElement;
                Assert.Equal(
                    "http://localhost:8000/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse",
                    root.GetProperty("requestUri").GetString()
                );
                Assert.Contains("\"systemInstruction\"", root.GetProperty("requestText").GetString(), StringComparison.Ordinal);
                Assert.Contains("\"text\":\"hello\"", root.GetProperty("responseText").GetString(), StringComparison.Ordinal);
            }

            using var replayHttpClient = CompletionHttpTransportFactory.CreateJsonLinesReplayClient(
                new Uri("http://localhost:8000/"),
                filePath
            );
            var replayed = await InvokeGeminiCompletionAsync(replayHttpClient, CreateRequest("gemini-2.5-flash"));
            Assert.Equal("hello world", replayed.Message.GetFlattenedText());
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
        if (!SupportsHardenedRawSink()) { return; }

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
    public async Task CapturePipeline_RedactsApiKeyQueryFromFailureExchange() {
        const string Secret = "GEMINI_QUERY_KEY_CANARY";
        var captureSink = new InMemoryCompletionHttpExchangeSink();
        using var httpClient = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(new ThrowingHttpMessageHandler(
                new HttpRequestException("simulated connect failure")
            ))
            .AddExchangeSink(captureSink)
            .Build();
        httpClient.BaseAddress = new Uri("https://provider.example/");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            httpClient.SendAsync(new HttpRequestMessage(
                HttpMethod.Get,
                $"v1beta/models/model?key={Secret}&alt=json"
            ))
        );

        CompletionHttpExchange exchange = Assert.Single(
            captureSink.GetSnapshot()
        );
        Assert.DoesNotContain(
            Secret,
            exchange.RequestUri,
            StringComparison.Ordinal
        );
        Assert.Equal(
            "https://provider.example/v1beta/models/model?key=%5BREDACTED%5D&alt=json",
            exchange.RequestUri
        );
    }

    [Fact]
    public async Task CapturePipeline_TransportFailure_IgnoresThrowingSinkAndPreservesOriginalException() {
        var originalFailure = new HttpRequestException(
            "original transport failure"
        );
        var captureSink = new InMemoryCompletionHttpExchangeSink();
        using var httpClient = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(new ThrowingHttpMessageHandler(originalFailure))
            .AddExchangeSink(
                new ThrowingCompletionHttpExchangeSink(
                    new OperationCanceledException("sink-only cancellation")
                )
            )
            .AddExchangeSink(captureSink)
            .Build();
        httpClient.BaseAddress = new Uri("http://localhost:8000/");

        HttpRequestException observedFailure = await Assert.ThrowsAsync<
            HttpRequestException
        >(() => httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "failure"),
            CancellationToken.None
        ));

        Assert.Same(originalFailure, observedFailure);
        CompletionHttpExchange exchange = Assert.Single(
            captureSink.GetSnapshot()
        );
        Assert.Null(exchange.StatusCode);
        Assert.Null(exchange.ResponseText);
        Assert.Equal(
            "System.Net.Http.HttpRequestException: original transport failure",
            exchange.ErrorText
        );
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
        if (!ShouldRunLocalLlmE2E()) { return; }

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
        if (!ShouldRunLocalLlmE2E()) { return; }

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
            modelId,
            new CompletionPromptPrefix(
                "system",
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new[] { new ObservationMessage("hello") }
            ),
            tailMessages: []
        );
    }

    private static bool ShouldRunLocalLlmE2E() {
        var value = Environment.GetEnvironmentVariable(LocalLlmE2EEnvVar);
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SupportsHardenedRawSink()
        => OperatingSystem.IsLinux()
            && RuntimeInformation.ProcessArchitecture is (
                Architecture.X64 or Architecture.Arm64);

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
                dialect: OpenAIChatDialects.QwenSgLang,
                options: new OpenAIChatClientOptions {
                    ReasoningEffort = CompletionReasoningEffort.Disabled
                }
            );

            var live = await openAiClient.StreamCompletionAsync(CreateRequest(modelId), null, CancellationToken.None);
            liveText = live.Message.GetFlattenedText();
        }

        string replayedText;
        using (var replayClient = CompletionHttpTransportFactory.CreateJsonLinesReplayClient(LocalLlmBaseAddress, filePath)) {
            var replayOpenAiClient = new OpenAIChatClient(
                apiKey: "ignored-local-api-key",
                httpClient: replayClient,
                dialect: OpenAIChatDialects.QwenSgLang,
                options: new OpenAIChatClientOptions {
                    ReasoningEffort = CompletionReasoningEffort.Disabled
                }
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
                httpClient: recordClient
            );

            var live = await anthropicClient.StreamCompletionAsync(CreateRequest(modelId), null, CancellationToken.None);
            liveText = live.Message.GetFlattenedText();
        }

        string replayedText;
        using (var replayClient = CompletionHttpTransportFactory.CreateJsonLinesReplayClient(LocalLlmBaseAddress, filePath)) {
            var replayAnthropicClient = new AnthropicClient(
                apiKey: "ignored-local-api-key",
                httpClient: replayClient
            );

            var replayed = await replayAnthropicClient.StreamCompletionAsync(CreateRequest(modelId), null, CancellationToken.None);
            replayedText = replayed.Message.GetFlattenedText();
        }

        return (liveText, replayedText);
    }

    private static async Task<CompletionResult> InvokeGeminiCompletionAsync(HttpClient httpClient, CompletionRequest request) {
        var client = CreateGeminiClient(httpClient);
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

    private static object CreateGeminiClient(HttpClient httpClient) {
        var clientType = typeof(CompletionHttpTransportFactory).Assembly.GetType("Atelia.Completion.Gemini.GeminiClient");
        Assert.NotNull(clientType);
        var constructor = clientType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(HasSupportedGeminiConstructorShape);

        Assert.NotNull(constructor);

        var arguments = constructor!
            .GetParameters()
            .Select(parameter => ResolveGeminiConstructorArgument(parameter, httpClient))
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

    private static object? ResolveGeminiConstructorArgument(ParameterInfo parameter, HttpClient httpClient) {
        if (parameter.ParameterType == typeof(HttpClient)) { return httpClient; }

        if (parameter.ParameterType == typeof(string) && string.Equals(parameter.Name, "apiKey", StringComparison.OrdinalIgnoreCase)) { return null; }

        if (parameter.HasDefaultValue) { return parameter.DefaultValue; }

        throw new InvalidOperationException(
            $"Unsupported GeminiClient constructor parameter '{parameter.Name}' of type '{parameter.ParameterType}'."
        );
    }

    private static bool GeminiProductionTypesPresent() {
        var assembly = typeof(CompletionHttpTransportFactory).Assembly;
        return assembly.GetType("Atelia.Completion.Gemini.GeminiClient") is not null;
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenForRead(string path, int flags);

    private sealed class StubHttpMessageHandler : HttpMessageHandler {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response) {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return Task.FromResult(_response);
        }
    }

    private sealed class CapabilityAwareStubHttpMessageHandler(
        string capabilityJson,
        HttpResponseMessage generationResponse
    ) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            _ = cancellationToken;
            return Task.FromResult(
                request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(
                            capabilityJson,
                            Encoding.UTF8,
                            "application/json"
                        )
                    }
                    : generationResponse
            );
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

    private sealed class ThrowingCompletionHttpExchangeSink(
        Exception exception
    ) : ICompletionHttpExchangeSink {
        public void OnExchange(CompletionHttpExchange exchange) {
            throw exception;
        }
    }

    private sealed class AnthropicReplayResponder : ICompletionHttpReplayResponder {
        public HttpResponseMessage CreateResponse(CompletionHttpReplayRequest request) {
            if (string.Equals(request.Method, "GET", StringComparison.Ordinal)) {
                Assert.Equal(
                    "http://localhost:8000/v1/models/claude-3-5-sonnet-20241022",
                    request.RequestUri
                );
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(
                        "{\"max_tokens\":200000}",
                        Encoding.UTF8,
                        "application/json"
                    )
                };
            }
            Assert.Equal("POST", request.Method);
            Assert.Equal("http://localhost:8000/v1/messages", request.RequestUri);
            Assert.Contains("\"model\":\"claude-3-5-sonnet-20241022\"", request.RequestText, StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    event: message_start
                    data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-3-5-sonnet-20241022","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

                    event: content_block_start
                    data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                    event: content_block_delta
                    data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"world"}}

                    event: content_block_stop
                    data: {"type":"content_block_stop","index":0}

                    event: message_delta
                    data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

                    event: message_stop
                    data: {"type":"message_stop"}

                    """
                    + "\n",
                    Encoding.UTF8,
                    "text/event-stream"
                )
            };
        }
    }
}
