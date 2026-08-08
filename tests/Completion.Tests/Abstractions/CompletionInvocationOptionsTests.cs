using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tests.Abstractions;

public sealed class CompletionInvocationOptionsTests {
    [Fact]
    public async Task DefaultInterfaceMethod_ConnectionDefaultFallsBackToLegacyOverload() {
        ICompletionClient client = new LegacyCompletionClient();

        CompletionResult result = await client.StreamCompletionAsync(
            CreateRequest(),
            CompletionInvocationOptions.Default,
            observer: null,
            CancellationToken.None
        );

        Assert.Equal("done", result.Message.GetFlattenedText());
        Assert.Equal(1, ((LegacyCompletionClient)client).CallCount);
    }

    [Theory]
    [InlineData(PromptCacheReuseHint.NoReuseExpected)]
    [InlineData(PromptCacheReuseHint.ReuseExpectedSoon)]
    [InlineData(PromptCacheReuseHint.ReuseExpectedAfterPause)]
    public async Task DefaultInterfaceMethod_NonDefaultHintFailsFast(
        PromptCacheReuseHint hint
    ) {
        ICompletionClient client = new LegacyCompletionClient();
        var options = new CompletionInvocationOptions {
            PromptCacheReuseHint = hint
        };

        NotSupportedException exception = await Assert.ThrowsAsync<
            NotSupportedException
        >(() => client.StreamCompletionAsync(
            CreateRequest(),
            options,
            observer: null,
            CancellationToken.None
        ));

        Assert.Contains(hint.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, ((LegacyCompletionClient)client).CallCount);
    }

    [Fact]
    public async Task DefaultInterfaceMethod_UnknownHintFailsFast() {
        ICompletionClient client = new LegacyCompletionClient();
        var options = new CompletionInvocationOptions {
            PromptCacheReuseHint = (PromptCacheReuseHint)int.MaxValue
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.StreamCompletionAsync(
                CreateRequest(),
                options,
                observer: null,
                CancellationToken.None
            )
        );

        Assert.Equal(0, ((LegacyCompletionClient)client).CallCount);
    }

    private static CompletionRequest CreateRequest() => new(
        "model-a",
        new CompletionPromptPrefix(
            "system",
            CompletionOutputContract.ProviderDefault([]),
            [new ObservationMessage("hello")]
        ),
        tailMessages: []
    );

    private sealed class LegacyCompletionClient : ICompletionClient {
        public string Name => "legacy";

        public string ApiSpecId => "legacy-v1";

        public int CallCount { get; private set; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(
                new CompletionResult(
                    new ActionMessage([new ActionBlock.Text("done")]),
                    CompletionDescriptor.From(this, request)
                )
            );
        }
    }
}
