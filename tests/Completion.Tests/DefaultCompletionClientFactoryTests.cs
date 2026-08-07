using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class DefaultCompletionClientFactoryTests {
    [Fact]
    public void CreateUsesAnInfiniteHttpClientTimeout() {
        var factory = new DefaultCompletionClientFactory();
        using var client = Assert.IsType<OwnedHttpCompletionClient>(
            factory.Create(Connection("default"))
        );

        Assert.Equal(Timeout.InfiniteTimeSpan, client.HttpClientTimeout);
    }

    [Fact]
    public async Task CallerCancellationRetainsCallerTokenIdentity() {
        var inner = new StalledStreamingClient();
        using var httpClient = new HttpClient();
        using var client = new OwnedHttpCompletionClient(
            inner,
            httpClient
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
        Assert.Equal(caller.Token, inner.ObservedToken);
    }

    [Fact]
    public void CreateRejectsExplicitReasoningOnGenericSgLangSurface() {
        var factory = new DefaultCompletionClientFactory();
        var connection = Connection("local") with {
            CompletionSurfaceId = "openai-chat/sglang-compatible",
            ReasoningEffort = CompletionReasoningEffort.High
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => factory.Create(connection)
        );

        Assert.Contains("openai-chat/qwen-sglang", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRejectsAnthropicPromptCacheTtlForOtherKinds() {
        var factory = new DefaultCompletionClientFactory();
        var connection = Connection("local") with {
            AnthropicPromptCacheTtl = AnthropicPromptCacheTtl.OneHour
        };

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException
        >(() => factory.Create(connection));

        Assert.Contains(
            "kind 'openai-chat' is not 'anthropic'",
            exception.Message,
            StringComparison.Ordinal
        );
    }

    private static CompletionConnectionConfig Connection(string id) => new(
        id,
        "openai-chat",
        "model-a",
        "openai-chat/strict",
        "http://localhost/"
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
