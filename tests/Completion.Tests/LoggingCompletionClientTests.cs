using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class LoggingCompletionClientTests : IDisposable {
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "atelia-completion-tests",
        Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        try {
            if (Directory.Exists(_tempDirectory)) {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Theory]
    [InlineData(CallLogFailureStage.Initialize, false)]
    [InlineData(CallLogFailureStage.Reserve, false)]
    [InlineData(CallLogFailureStage.Write, false)]
    [InlineData(CallLogFailureStage.Flush, false)]
    [InlineData(CallLogFailureStage.Dispose, false)]
    [InlineData(CallLogFailureStage.Cleanup, false)]
    [InlineData(CallLogFailureStage.Initialize, true)]
    [InlineData(CallLogFailureStage.Reserve, true)]
    [InlineData(CallLogFailureStage.Write, true)]
    [InlineData(CallLogFailureStage.Flush, true)]
    [InlineData(CallLogFailureStage.Dispose, true)]
    [InlineData(CallLogFailureStage.Cleanup, true)]
    public async Task CallLogFailure_PreservesProviderOutcome(
        CallLogFailureStage failureStage,
        bool providerThrows
    ) {
        var expectedResult = new CompletionResult(
            new ActionMessage([new ActionBlock.Text("provider result")]),
            new CompletionDescriptor("scripted", "test-api-v1", "model-a")
        );
        var expectedException = new InvalidOperationException(
            "provider exception"
        );
        var inner = new ScriptedCompletionClient(
            expectedResult,
            providerThrows ? expectedException : null
        );
        var sink = new FaultInjectingCallLogSink(
            _tempDirectory,
            failureStage
        );
        LoggingCompletionClient client = CreateLoggingClient(inner, sink);

        if (providerThrows) {
            var actualException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.StreamCompletionAsync(CreateRequest(), observer: null)
            );
            Assert.Same(expectedException, actualException);
        }
        else {
            CompletionResult actualResult = await client.StreamCompletionAsync(
                CreateRequest(),
                observer: null
            );
            Assert.Same(expectedResult, actualResult);
        }

        Assert.Equal(1, inner.CallCount);
        Assert.Empty(client.WrittenCallLogPaths);
        if (Directory.Exists(_tempDirectory)) {
            string[] files = Directory
                .EnumerateFiles(_tempDirectory, "*.json")
                .ToArray();
            if (failureStage == CallLogFailureStage.Cleanup) {
                Assert.Single(files);
            }
            else {
                Assert.Empty(files);
            }
        }
    }

    [Theory]
    [InlineData(CallLogFailureStage.Reserve)]
    [InlineData(CallLogFailureStage.Write)]
    [InlineData(CallLogFailureStage.Flush)]
    [InlineData(CallLogFailureStage.Dispose)]
    public async Task TransientCallLogFailure_DoesNotPoisonLaterLogging(
        CallLogFailureStage failureStage
    ) {
        var expectedResult = new CompletionResult(
            new ActionMessage([new ActionBlock.Text("provider result")]),
            new CompletionDescriptor("scripted", "test-api-v1", "model-a")
        );
        var inner = new ScriptedCompletionClient(expectedResult, exception: null);
        var sink = new FaultInjectingCallLogSink(
            _tempDirectory,
            failureStage
        );
        LoggingCompletionClient client = CreateLoggingClient(inner, sink);

        Assert.Same(
            expectedResult,
            await client.StreamCompletionAsync(CreateRequest(), observer: null)
        );
        Assert.Empty(client.WrittenCallLogPaths);

        Assert.Same(
            expectedResult,
            await client.StreamCompletionAsync(CreateRequest(), observer: null)
        );
        string writtenPath = Assert.Single(client.WrittenCallLogPaths);
        Assert.Equal("0002.json", Path.GetFileName(writtenPath));
        Assert.True(File.Exists(writtenPath));
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task InitializationFailure_DisablesLoggingForWrapperLifetime() {
        var expectedResult = CreateResult("provider result");
        var inner = new ScriptedCompletionClient(expectedResult, exception: null);
        var sink = new FaultInjectingCallLogSink(
            _tempDirectory,
            CallLogFailureStage.Initialize
        );
        LoggingCompletionClient client = CreateLoggingClient(inner, sink);

        Assert.Same(
            expectedResult,
            await client.StreamCompletionAsync(CreateRequest(), observer: null)
        );
        Assert.Same(
            expectedResult,
            await client.StreamCompletionAsync(CreateRequest(), observer: null)
        );

        Assert.Equal(2, inner.CallCount);
        Assert.Equal(0, sink.ReserveCallCount);
        Assert.Empty(client.WrittenCallLogPaths);
        Assert.False(Directory.Exists(_tempDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingFailureReporter_PreservesProviderOutcome(
        bool providerThrows
    ) {
        var expectedResult = CreateResult("provider result");
        var expectedException = new InvalidOperationException(
            "provider exception"
        );
        var inner = new ScriptedCompletionClient(
            expectedResult,
            providerThrows ? expectedException : null
        );
        var sink = new FaultInjectingCallLogSink(
            _tempDirectory,
            CallLogFailureStage.Reserve
        );
        var reporterCallCount = 0;
        LoggingCompletionClient client = CreateLoggingClient(
            inner,
            sink,
            (stage, exception) => {
                _ = stage;
                _ = exception;
                Interlocked.Increment(ref reporterCallCount);
                throw new IOException("scripted reporter failure");
            }
        );

        if (providerThrows) {
            var actualException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.StreamCompletionAsync(CreateRequest(), observer: null)
            );
            Assert.Same(expectedException, actualException);
        }
        else {
            Assert.Same(
                expectedResult,
                await client.StreamCompletionAsync(
                    CreateRequest(),
                    observer: null
                )
            );
        }

        Assert.Equal(1, reporterCallCount);
        Assert.Equal(1, inner.CallCount);
        Assert.Empty(client.WrittenCallLogPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestLogProjectionFailure_PreservesProviderOutcome(
        bool providerThrows
    ) {
        var expectedResult = CreateResult("provider result");
        var expectedException = new InvalidOperationException(
            "provider exception"
        );
        var inner = new ScriptedCompletionClient(
            expectedResult,
            providerThrows ? expectedException : null
        );
        LoggingCompletionClient client = CreateLoggingClient(inner);
        CompletionRequest request = CreateRequest(new ThrowingHistoryMessage());

        if (providerThrows) {
            var actualException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.StreamCompletionAsync(request, observer: null)
            );
            Assert.Same(expectedException, actualException);
        }
        else {
            Assert.Same(
                expectedResult,
                await client.StreamCompletionAsync(request, observer: null)
            );
        }

        Assert.Equal(1, inner.CallCount);
        Assert.Empty(client.WrittenCallLogPaths);
        Assert.Empty(Directory.EnumerateFiles(_tempDirectory, "*.json"));
    }

    [Fact]
    public async Task ProviderException_RetainsInnerStackMarkerWhenLoggingFails() {
        var expectedException = new InvalidOperationException(
            "provider exception"
        );
        var inner = new MarkerThrowingCompletionClient(expectedException);
        LoggingCompletionClient client = CreateLoggingClient(
            inner,
            new FaultInjectingCallLogSink(
                _tempDirectory,
                CallLogFailureStage.Write
            )
        );

        var actualException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StreamCompletionAsync(CreateRequest(), observer: null)
        );

        Assert.Same(expectedException, actualException);
        Assert.Contains(
            "ThrowFromInnerMarker",
            actualException.StackTrace ?? string.Empty,
            StringComparison.Ordinal
        );
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task ProviderCancellation_RetainsExceptionInstanceAndToken() {
        using var source = new CancellationTokenSource();
        var expectedException = new OperationCanceledException(
            "provider cancellation",
            innerException: null,
            token: source.Token
        );
        var inner = new ScriptedCompletionClient(
            CreateResult("unused"),
            expectedException
        );
        LoggingCompletionClient client = CreateLoggingClient(
            inner,
            new FaultInjectingCallLogSink(
                _tempDirectory,
                CallLogFailureStage.Flush
            )
        );

        var actualException = await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.StreamCompletionAsync(CreateRequest(), observer: null)
        );

        Assert.Same(expectedException, actualException);
        Assert.Equal(source.Token, actualException.CancellationToken);
        Assert.Equal(1, inner.CallCount);
        Assert.Empty(client.WrittenCallLogPaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankCallLogDirectory_RemainsAContractError(
        string? callLogDirectory
    ) {
        var exception = Assert.ThrowsAny<ArgumentException>(
            () => new LoggingCompletionClient(
                new YieldingCompletionClient("contract-test"),
                CreateConnection(),
                callLogDirectory!
            )
        );

        Assert.Equal("callLogDir", exception.ParamName);
    }

    [Fact]
    public async Task SharedDirectory_ReservesUniquePathsAcrossClientsAndConcurrentCalls() {
        var first = CreateLoggingClient(new YieldingCompletionClient("first"));
        var second = CreateLoggingClient(new YieldingCompletionClient("second"));
        CompletionRequest request = CreateRequest();
        var calls = new List<Task<CompletionResult>>();
        for (int i = 0; i < 4; i++) {
            calls.Add(first.StreamCompletionAsync(request, observer: null));
            calls.Add(second.StreamCompletionAsync(request, observer: null));
        }

        await Task.WhenAll(calls);

        IReadOnlyList<string> firstPaths = first.WrittenCallLogPaths;
        IReadOnlyList<string> secondPaths = second.WrittenCallLogPaths;
        Assert.Equal(4, firstPaths.Count);
        Assert.Equal(4, secondPaths.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)firstPaths).Add("unexpected"));

        string[] allPaths = [.. firstPaths, .. secondPaths];
        Assert.Equal(allPaths.Length, allPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            allPaths.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(_tempDirectory, "*.json").Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            Enumerable.Range(1, 8).ToArray(),
            allPaths
                .Select(static path => int.Parse(
                    Path.GetFileNameWithoutExtension(path),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture
                ))
                .Order()
                .ToArray()
        );

        foreach (string path in allPaths) {
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.True(File.Exists(path));
            int filenameCallId = int.Parse(
                Path.GetFileNameWithoutExtension(path),
                NumberStyles.None,
                CultureInfo.InvariantCulture
            );
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(
                "atelia.completion.call-log.v3",
                document.RootElement.GetProperty("schema").GetString()
            );
            Assert.Equal(filenameCallId, document.RootElement.GetProperty("callId").GetInt32());
        }
    }

    [Fact]
    public async Task SuccessfulCompletion_RecordsCanonicalRequestAndResponse() {
        var client = CreateLoggingClient(
            new YieldingCompletionClient("content-test")
        );

        _ = await client.StreamCompletionAsync(
            CreateRequest(),
            observer: null
        );

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Assert.Single(client.WrittenCallLogPaths))
        );
        JsonElement root = document.RootElement;
        Assert.Equal(
            "atelia.completion.call-log.v3",
            root.GetProperty("schema").GetString()
        );
        JsonElement request = root.GetProperty("request");
        Assert.Equal("model-a", request.GetProperty("modelId").GetString());
        Assert.Equal("system", request.GetProperty("systemPrompt").GetString());
        JsonElement history = Assert.Single(
            request.GetProperty("context").EnumerateArray()
        );
        Assert.Equal("observation", history.GetProperty("kind").GetString());
        Assert.Equal("hello", history.GetProperty("content").GetString());
        Assert.Empty(request.GetProperty("tools").EnumerateArray());
        JsonElement connection = root.GetProperty("connection");
        Assert.Equal(4096, connection.GetProperty("maxTokens").GetInt32());
        Assert.Equal(
            "high",
            connection.GetProperty("reasoningEffort").GetString()
        );
        Assert.False(
            connection.TryGetProperty(
                "effectiveRequestTimeoutSeconds",
                out _
            )
        );

        JsonElement response = root.GetProperty("response");
        Assert.Equal("done", response.GetProperty("text").GetString());
        Assert.Equal(
            "content-test",
            response
                .GetProperty("invocation")
                .GetProperty("providerId")
                .GetString()
        );
        Assert.Equal(
            "completed",
            response
                .GetProperty("termination")
                .GetProperty("kind")
                .GetString()
        );
        JsonElement actionBlock = Assert.Single(
            response.GetProperty("actionBlocks").EnumerateArray()
        );
        Assert.Equal("text", actionBlock.GetProperty("kind").GetString());
        Assert.Equal("done", actionBlock.GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task FailedCompletion_RecordsItsActualPathOnTheOwningClient() {
        var client = CreateLoggingClient(new ThrowingCompletionClient());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StreamCompletionAsync(CreateRequest(), observer: null)
        );

        Assert.Equal("scripted completion failure", ex.Message);
        string path = Assert.Single(client.WrittenCallLogPaths);
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.True(File.Exists(path));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        Assert.Equal(
            "atelia.completion.call-log.v3",
            root.GetProperty("schema").GetString()
        );
        Assert.False(root.TryGetProperty("response", out _));
        Assert.Equal(
            typeof(InvalidOperationException).FullName,
            root.GetProperty("exception").GetProperty("type").GetString()
        );
        Assert.Equal(
            "scripted completion failure",
            root.GetProperty("exception").GetProperty("message").GetString()
        );
        Assert.False(
            root.GetProperty("connection").TryGetProperty(
                "effectiveRequestTimeoutSeconds",
                out _
            )
        );
    }

    private LoggingCompletionClient CreateLoggingClient(
        ICompletionClient inner
    )
        => new(
            inner,
            CreateConnection(),
            _tempDirectory
        );

    private LoggingCompletionClient CreateLoggingClient(
        ICompletionClient inner,
        ICompletionCallLogSink sink,
        Action<string, Exception>? reporter = null
    )
        => new(
            inner,
            CreateConnection(),
            _tempDirectory,
            context: null,
            () => sink,
            reporter
        );

    private static CompletionConnectionConfig CreateConnection() => new(
        Id: "test",
        Kind: "scripted",
        ModelId: "model-a",
        CompletionSurfaceId: "surface-a",
        BaseAddress: "http://localhost/",
        MaxTokens: 4096,
        ReasoningEffort: CompletionReasoningEffort.High
    );

    private static CompletionRequest CreateRequest()
        => CreateRequest(new ObservationMessage("hello"));

    private static CompletionRequest CreateRequest(IHistoryMessage message)
        => new(
            ModelId: "model-a",
            SystemPrompt: "system",
            Context: [message],
            Tools: []
        );

    private static CompletionResult CreateResult(string text)
        => new(
            new ActionMessage([new ActionBlock.Text(text)]),
            new CompletionDescriptor("scripted", "test-api-v1", "model-a")
        );

    private sealed class YieldingCompletionClient(string name) : ICompletionClient {
        public string Name { get; } = name;

        public string ApiSpecId => "test-api-v1";

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return new CompletionResult(
                new ActionMessage([new ActionBlock.Text("done")]),
                CompletionDescriptor.From(this, request)
            );
        }
    }

    private sealed class ThrowingCompletionClient : ICompletionClient {
        public string Name => "throwing";

        public string ApiSpecId => "test-api-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("scripted completion failure");
        }
    }

    public enum CallLogFailureStage {
        Initialize,
        Reserve,
        Write,
        Flush,
        Dispose,
        Cleanup,
    }

    private sealed class ScriptedCompletionClient(
        CompletionResult result,
        Exception? exception
    ) : ICompletionClient {
        private readonly CompletionResult _result = result;
        private readonly Exception? _exception = exception;
        private int _callCount;

        public string Name => "scripted";

        public string ApiSpecId => "test-api-v1";

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            if (_exception is not null) {
                throw _exception;
            }
            return Task.FromResult(_result);
        }
    }

    private sealed class MarkerThrowingCompletionClient(
        InvalidOperationException exception
    ) : ICompletionClient {
        private readonly InvalidOperationException _exception = exception;
        private int _callCount;

        public string Name => "marker-throwing";

        public string ApiSpecId => "test-api-v1";

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return ThrowFromInnerMarker(_exception);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task<CompletionResult> ThrowFromInnerMarker(
            InvalidOperationException exception
        ) => throw exception;
    }

    private sealed class ThrowingHistoryMessage : IHistoryMessage {
        public HistoryMessageKind Kind => HistoryMessageKind.ContextHeader;

        public override string ToString()
            => throw new InvalidOperationException(
                "scripted request log projection failure"
            );
    }

    private sealed class FaultInjectingCallLogSink(
        string callLogDirectory,
        CallLogFailureStage failureStage
    ) : ICompletionCallLogSink {
        private readonly string _callLogDirectory = callLogDirectory;
        private readonly CallLogFailureStage _failureStage = failureStage;
        private int _remainingFailures = 1;
        private int _nextCallId;
        private int _reserveCallCount;

        public int ReserveCallCount => Volatile.Read(ref _reserveCallCount);

        public void Initialize(string ignoredCallLogDirectory) {
            _ = ignoredCallLogDirectory;
            if (TryFail(CallLogFailureStage.Initialize)) {
                throw new IOException("scripted initialize failure");
            }
            Directory.CreateDirectory(_callLogDirectory);
        }

        public CompletionCallLogReservation Reserve() {
            Interlocked.Increment(ref _reserveCallCount);
            int callId = Interlocked.Increment(ref _nextCallId);
            if (TryFail(CallLogFailureStage.Reserve)) {
                throw new IOException("scripted reserve failure");
            }

            string path = Path.Combine(
                _callLogDirectory,
                $"{callId:0000}.json"
            );
            Stream stream = new FaultInjectingStream(
                new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read
                ),
                () => TryFail(CallLogFailureStage.Write)
                    || _failureStage == CallLogFailureStage.Cleanup,
                () => TryFail(CallLogFailureStage.Flush),
                () => TryFail(CallLogFailureStage.Dispose)
            );
            Action<string>? cleanup =
                _failureStage == CallLogFailureStage.Cleanup
                    ? static _ => throw new IOException(
                        "scripted cleanup failure"
                    )
                    : null;
            return new CompletionCallLogReservation(
                callId,
                path,
                stream,
                cleanup
            );
        }

        private bool TryFail(CallLogFailureStage stage)
            => _failureStage == stage
                && Interlocked.Exchange(ref _remainingFailures, 0) == 1;
    }

    private sealed class FaultInjectingStream(
        Stream inner,
        Func<bool> failWrite,
        Func<bool> failFlush,
        Func<bool> failDispose
    ) : Stream {
        private readonly Stream _inner = inner;
        private readonly Func<bool> _failWrite = failWrite;
        private readonly Func<bool> _failFlush = failFlush;
        private readonly Func<bool> _failDispose = failDispose;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() {
            if (_failFlush()) {
                throw new IOException("scripted flush failure");
            }
            _inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
            => _inner.Seek(offset, origin);

        public override void SetLength(long value)
            => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) {
            if (_failWrite()) {
                throw new IOException("scripted write failure");
            }
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer) {
            if (_failWrite()) {
                throw new IOException("scripted write failure");
            }
            _inner.Write(buffer);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                _inner.Dispose();
                if (_failDispose()) {
                    throw new IOException("scripted dispose failure");
                }
            }
            base.Dispose(disposing);
        }
    }
}
