using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class CompletionConnectionRegistryLifetimeTests {
    [Fact]
    public async Task DisposeAsync_DisposesDistinctAsyncClientsExactlyOnce() {
        var client = new AsyncClient();
        var registry = new CompletionConnectionRegistry(
            Config("a", "b"),
            new SharedFactory(client)
        );

        Assert.Same(client, registry.GetClient("a"));
        Assert.Same(client, registry.GetClient("b"));

        await registry.DisposeAsync();
        await registry.DisposeAsync();
        registry.Dispose();

        Assert.Equal(1, client.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() =>
            registry.GetClient("a"));
    }

    [Fact]
    public void Dispose_DisposesDistinctSyncClientsExactlyOnce() {
        var client = new SyncClient();
        var registry = new CompletionConnectionRegistry(
            Config("a", "b"),
            new SharedFactory(client)
        );
        registry.GetClient("a");
        registry.GetClient("b");

        registry.Dispose();
        registry.Dispose();

        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public void Dispose_ContinuesAfterFailureAndRethrowsAtEnd() {
        var first = new ThrowingSyncClient("first");
        var second = new SyncClient();
        var registry = new CompletionConnectionRegistry(
            Config("a", "b"),
            new SequenceFactory(first, second)
        );
        registry.GetClient("a");
        registry.GetClient("b");

        InvalidOperationException failure = Assert.Throws<
            InvalidOperationException
        >(() => registry.Dispose());
        Assert.Equal("first", failure.Message);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        registry.Dispose();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ContinuesAndAggregatesAllFailures() {
        var first = new ThrowingAsyncClient("first");
        var second = new ThrowingAsyncClient("second");
        var registry = new CompletionConnectionRegistry(
            Config("a", "b"),
            new SequenceFactory(first, second)
        );
        registry.GetClient("a");
        registry.GetClient("b");

        AggregateException failure = await Assert.ThrowsAsync<
            AggregateException
        >(() => registry.DisposeAsync().AsTask());
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        await registry.DisposeAsync();
    }

    [Fact]
    public void Dispose_FatalCleanupFailurePropagatesImmediately() {
        var recoverable = new ThrowingSyncClient("recoverable");
        var fatal = new FatalSyncClient();
        var notReached = new SyncClient();
        var registry = new CompletionConnectionRegistry(
            Config("a", "b", "c"),
            new SequenceFactory(recoverable, fatal, notReached)
        );
        registry.GetClient("a");
        registry.GetClient("b");
        registry.GetClient("c");

        Assert.Throws<OutOfMemoryException>(() => registry.Dispose());
        Assert.Equal(1, recoverable.DisposeCount);
        Assert.Equal(1, fatal.DisposeCount);
        Assert.Equal(0, notReached.DisposeCount);
        registry.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_FatalCleanupFailurePropagatesImmediately() {
        var recoverable = new ThrowingAsyncClient("recoverable");
        var fatal = new FatalAsyncClient();
        var notReached = new AsyncClient();
        var registry = new CompletionConnectionRegistry(
            Config("a", "b", "c"),
            new SequenceFactory(recoverable, fatal, notReached)
        );
        registry.GetClient("a");
        registry.GetClient("b");
        registry.GetClient("c");

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            registry.DisposeAsync().AsTask());
        Assert.Equal(1, recoverable.DisposeCount);
        Assert.Equal(1, fatal.DisposeCount);
        Assert.Equal(0, notReached.DisposeCount);
        await registry.DisposeAsync();
    }

    private static CompletionConnectionsFileConfig Config(
        params string[] ids
    ) => new(
        ids.Select(id => new CompletionConnectionConfig(
            id,
            "test",
            $"model-{id}",
            "test-v1",
            "https://example.invalid/"
        )).ToArray(),
        ids[0]
    );

    private sealed class SharedFactory(ICompletionClient client)
        : ICompletionClientFactory {
        public ICompletionClient Create(CompletionConnectionConfig connection)
            => client;
    }

    private sealed class SequenceFactory(params ICompletionClient[] clients)
        : ICompletionClientFactory {
        private int _next;
        public ICompletionClient Create(CompletionConnectionConfig connection)
            => clients[_next++];
    }

    private abstract class ClientBase : ICompletionClient {
        public string Name => "lifetime-test";
        public string ApiSpecId => "lifetime-test-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class AsyncClient : ClientBase, IAsyncDisposable {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync() {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyncClient : ClientBase, IDisposable {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingSyncClient(string message)
        : ClientBase, IDisposable {
        public int DisposeCount { get; private set; }
        public void Dispose() {
            DisposeCount++;
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ThrowingAsyncClient(string message)
        : ClientBase, IAsyncDisposable {
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync() {
            DisposeCount++;
            return ValueTask.FromException(
                new InvalidOperationException(message)
            );
        }
    }

    private sealed class FatalSyncClient : ClientBase, IDisposable {
        public int DisposeCount { get; private set; }
        public void Dispose() {
            DisposeCount++;
            throw new OutOfMemoryException("fatal-sync");
        }
    }

    private sealed class FatalAsyncClient : ClientBase, IAsyncDisposable {
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync() {
            DisposeCount++;
            return ValueTask.FromException(
                new OutOfMemoryException("fatal-async")
            );
        }
    }
}
