using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.ModelSpecs;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class DefaultCompletionClientFactoryTests {
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task StrictConnectionLoader_ToGeminiFactory_PreservesEffortAndSelectsCatalogOnce(int version) {
        string defaultField = version == 2 ? ",\"defaultConnectionId\":\"gemini\"" : "";
        byte[] json = Encoding.UTF8.GetBytes($$"""
            {"v":{{version}},"connections":[{"id":"gemini","kind":"gemini","modelId":"model-a",
            "completionSurfaceId":"gemini","baseAddress":"https://example.invalid/","reasoningEffort":"medium"}]{{defaultField}}}
            """);
        CompletionConnectionConfig connection = version == 2
            ? Assert.Single(CompletionConnectionConfigLoader.Decode(json).Connections)
            : Assert.Single(CompletionConnectionConfigLoader.DecodeCatalog(json).Connections);
        var mapper = new RejectingMapper();
        int selections = 0;
        var factory = new DefaultCompletionClientFactory(config => {
            Assert.Same(connection, config);
            selections++;
            return CompletionModelSpecCatalog.Empty.WithModel("model-a", new() { ReasoningMapper = mapper });
        });
        using var client = Assert.IsType<OwnedHttpCompletionClient>(factory.Create(connection));
        Assert.Equal("google-gemini-generate-content-v1beta", client.ApiSpecId);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.HttpClientTimeout);
        for (int i = 0; i < 2; i++) {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => client.StreamCompletionAsync(Request(), null));
            Assert.Equal("fixture.mapper_invoked", error.Message);
        }
        Assert.Equal(1, selections);
        Assert.Equal(2, mapper.Calls);
    }

    [Theory]
    [InlineData("openai-chat", "openai-chat/strict")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("gemini", "gemini")]
    public async Task ModelSpecsAreSelectedOnceAndReachTheConcreteClient(string kind, string surface) {
        var mapper = new RejectingMapper();
        var catalog = CompletionModelSpecCatalog.Empty.WithModel(
            "model-a", new CompletionModelSpec { ReasoningMapper = mapper });
        var connection = Connection("selected") with {
            Kind = kind,
            CompletionSurfaceId = surface,
            ReasoningEffort = CompletionReasoningEffort.Medium
        };
        int selections = 0;
        var factory = new DefaultCompletionClientFactory(config => {
            Assert.Same(connection, config);
            selections++;
            return catalog;
        });
        using var client = Assert.IsType<OwnedHttpCompletionClient>(factory.Create(connection));

        for (int i = 0; i < 2; i++) {
            var error = await Assert.ThrowsAsync<ArgumentException>(
                () => client.StreamCompletionAsync(Request(), observer: null));
            Assert.Equal("fixture.mapper_invoked", error.Message);
        }
        Assert.Equal(1, selections);
        Assert.Equal(2, mapper.Calls);
    }

    [Fact]
    public void ResponsesDoesNotSelectModelSpecs() {
        var factory = new DefaultCompletionClientFactory(_ =>
            throw new InvalidOperationException("Unexpected selection."));
        using var client = Assert.IsType<OwnedHttpCompletionClient>(factory.Create(
            Connection("responses") with { Kind = "openai-responses", CompletionSurfaceId = "openai-responses" }));
        Assert.Equal("openai-responses-v2", client.ApiSpecId);
    }

    [Fact]
    public void SelectorFailurePrecedesTransportConstruction() {
        var factory = new DefaultCompletionClientFactory(_ =>
            throw new ArgumentException("fixture.selection_failed"));
        var error = Assert.Throws<ArgumentException>(() => factory.Create(
            Connection("invalid-endpoint") with { BaseAddress = "not an absolute uri" }));
        Assert.Equal("fixture.selection_failed", error.Message);
    }

    private sealed class RejectingMapper : ICompletionReasoningEffortMapper {
        public int Calls { get; private set; }
        public ReasoningEffortMapping Map(CompletionReasoningEffort requested) {
            Assert.Equal(CompletionReasoningEffort.Medium, requested);
            Calls++;
            throw new ArgumentException("fixture.mapper_invoked");
        }
    }

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
    public async Task OwnedClient_ForwardsInvocationOptionsToInnerClient() {
        var inner = new InvocationOptionsCapturingClient();
        using var httpClient = new HttpClient();
        using var client = new OwnedHttpCompletionClient(inner, httpClient);
        var options = new CompletionInvocationOptions {
            PromptCacheReuseHint = PromptCacheReuseHint.NoReuseExpected
        };

        _ = await client.StreamCompletionAsync(
            Request(),
            options,
            observer: null,
            CancellationToken.None
        );

        Assert.Same(options, inner.ObservedOptions);
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
        new CompletionPromptPrefix(
            "system-a",
            CompletionOutputContract.ProviderDefault(
                ImmutableArray<ToolDefinition>.Empty
            ),
            Array.Empty<IHistoryMessage>()
        ),
        tailMessages: []
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

    private sealed class InvocationOptionsCapturingClient : ICompletionClient {
        public string Name => "capturing";

        public string ApiSpecId => "test-v1";

        public CompletionInvocationOptions? ObservedOptions { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Expected invocation options overload.");

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionInvocationOptions invocationOptions,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            ObservedOptions = invocationOptions;
            return Task.FromResult(
                new CompletionResult(
                    new ActionMessage([new ActionBlock.Text("done")]),
                    CompletionDescriptor.From(this, request)
                )
            );
        }
    }
}
