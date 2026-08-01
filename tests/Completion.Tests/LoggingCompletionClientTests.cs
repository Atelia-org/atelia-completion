using System.Globalization;
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

        foreach (string path in allPaths) {
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.True(File.Exists(path));
            int filenameCallId = int.Parse(
                Path.GetFileNameWithoutExtension(path),
                NumberStyles.None,
                CultureInfo.InvariantCulture
            );
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(filenameCallId, document.RootElement.GetProperty("callId").GetInt32());
        }
    }

    [Fact]
    public async Task FailedCompletion_RecordsItsActualPathOnTheOwningClient() {
        var client = CreateLoggingClient(
            new ThrowingCompletionClient(),
            requestTimeoutSeconds: 300
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StreamCompletionAsync(CreateRequest(), observer: null)
        );

        Assert.Equal("scripted completion failure", ex.Message);
        string path = Assert.Single(client.WrittenCallLogPaths);
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.True(File.Exists(path));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            typeof(InvalidOperationException).FullName,
            document.RootElement.GetProperty("exception").GetProperty("type").GetString()
        );
        Assert.Equal(
            300,
            document.RootElement
                .GetProperty("connection")
                .GetProperty("effectiveRequestTimeoutSeconds")
                .GetInt32()
        );
    }

    [Fact]
    public async Task DefaultTimeoutIsReportedAsEffectiveOneHundredSeconds() {
        var client = CreateLoggingClient(
            new YieldingCompletionClient("default-timeout")
        );

        _ = await client.StreamCompletionAsync(
            CreateRequest(),
            observer: null
        );

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(Assert.Single(client.WrittenCallLogPaths))
        );
        Assert.Equal(
            100,
            document.RootElement
                .GetProperty("connection")
                .GetProperty("effectiveRequestTimeoutSeconds")
                .GetInt32()
        );
    }

    private LoggingCompletionClient CreateLoggingClient(
        ICompletionClient inner,
        int? requestTimeoutSeconds = null
    )
        => new(
            inner,
            new CompletionConnectionConfig(
                Id: "test",
                Kind: "scripted",
                ModelId: "model-a",
                CompletionSurfaceId: "surface-a",
                BaseAddress: "http://localhost/",
                RequestTimeoutSeconds: requestTimeoutSeconds
            ),
            _tempDirectory
        );

    private static CompletionRequest CreateRequest()
        => new(
            ModelId: "model-a",
            SystemPrompt: "system",
            Context: [new ObservationMessage("hello")],
            Tools: []
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
}
