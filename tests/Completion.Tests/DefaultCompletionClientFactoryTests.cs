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
            TimeSpan.FromSeconds(300),
            extendedClient.HttpRequestTimeout
        );
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
}
