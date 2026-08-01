using System.Collections.Immutable;
using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class DefaultCompletionClientFactoryTests {
    [Fact]
    public void CreateAppliesOnlyTheSelectedConnectionsEffectiveTimeout() {
        var factory = new DefaultCompletionClientFactory();
        using var defaultClient = Assert.IsType<OwnedHttpCompletionClient>(
            factory.Create(Connection("default", requestTimeoutSeconds: null))
        );
        using var extendedClient = Assert.IsType<OwnedHttpCompletionClient>(
            factory.Create(Connection("extended", requestTimeoutSeconds: 300))
        );

        Assert.Equal(
            TimeSpan.FromSeconds(100),
            defaultClient.HttpRequestTimeout
        );
        Assert.Equal(
            TimeSpan.FromSeconds(100),
            defaultClient.WholeOperationTimeout
        );
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            extendedClient.HttpRequestTimeout
        );
        Assert.Equal(
            TimeSpan.FromSeconds(300),
            extendedClient.WholeOperationTimeout
        );
    }

    [Fact]
    public async Task WholeOperationTimeoutCancelsAStalledStreamingInner() {
        var inner = new StalledStreamingClient();
        using var httpClient = new HttpClient {
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        using var client = new OwnedHttpCompletionClient(
            inner,
            httpClient,
            TimeSpan.FromMilliseconds(50)
        );
        var stopwatch = Stopwatch.StartNew();

        TaskCanceledException exception = await Assert.ThrowsAsync<
            TaskCanceledException
        >(() => client.StreamCompletionAsync(Request(), observer: null));

        stopwatch.Stop();
        Assert.True(inner.Entered);
        Assert.True(inner.ObservedToken.CanBeCanceled);
        Assert.True(inner.ObservedToken.IsCancellationRequested);
        Assert.Contains(
            "configured whole-operation timeout",
            exception.Message,
            StringComparison.Ordinal
        );
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Timeout took unexpectedly long: {stopwatch.Elapsed}."
        );
    }

    [Fact]
    public async Task CallerCancellationRetainsCallerTokenIdentity() {
        var inner = new StalledStreamingClient();
        using var httpClient = new HttpClient {
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var client = new OwnedHttpCompletionClient(
            inner,
            httpClient,
            TimeSpan.FromSeconds(30)
        );
        using var caller = new CancellationTokenSource();
        Task<CompletionResult> operation = client.StreamCompletionAsync(
            Request(),
            observer: null,
            caller.Token
        );
        caller.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<
            OperationCanceledException
        >(() => operation);

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.True(inner.Entered);
    }

    private static CompletionConnectionConfig Connection(
        string id,
        int? requestTimeoutSeconds
    ) => new(
        id,
        "openai-chat",
        "model-a",
        "openai-chat/strict",
        "http://localhost/",
        RequestTimeoutSeconds: requestTimeoutSeconds
    );

    private static CompletionRequest Request() => new(
        "model-a",
        "system-a",
        Array.Empty<IHistoryMessage>(),
        ImmutableArray<ToolDefinition>.Empty
    );

    private sealed class StalledStreamingClient : ICompletionClient {
        public string Name => "stalled-after-headers";

        public string ApiSpecId => "test-stream-v1";

        public bool Entered { get; private set; }

        public CancellationToken ObservedToken { get; private set; }

        public async Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            Entered = true;
            ObservedToken = cancellationToken;
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken
            );
            throw new InvalidOperationException(
                "An infinite streaming wait returned without cancellation."
            );
        }
    }
}
