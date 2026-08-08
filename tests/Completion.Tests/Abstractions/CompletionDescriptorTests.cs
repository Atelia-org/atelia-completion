using System.Collections.Immutable;
using Xunit;

namespace Atelia.Completion.Abstractions.Tests;

public sealed class CompletionDescriptorTests {
    [Fact]
    public void FromClientAndRequest_UsesCanonicalClientIdentity() {
        var client = new StubCompletionClient("provider-x", "spec-y");
        var request = new CompletionRequest(
            "model-z",
            new CompletionPromptPrefix(
                "system",
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                Array.Empty<IHistoryMessage>()
            ),
            tailMessages: []
        );

        var descriptor = CompletionDescriptor.From(client, request);

        Assert.Equal("provider-x", descriptor.ProviderId);
        Assert.Equal("spec-y", descriptor.ApiSpecId);
        Assert.Equal("model-z", descriptor.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_BlankFields_Throw(string blank) {
        Assert.Throws<ArgumentException>(() => new CompletionDescriptor(blank, "spec", "model"));
        Assert.Throws<ArgumentException>(() => new CompletionDescriptor("provider", blank, "model"));
        Assert.Throws<ArgumentException>(() => new CompletionDescriptor("provider", "spec", blank));
    }

    private sealed class StubCompletionClient : ICompletionClient {
        public StubCompletionClient(string name, string apiSpecId) {
            Name = name;
            ApiSpecId = apiSpecId;
        }

        public string Name { get; }
        public string ApiSpecId { get; }

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
