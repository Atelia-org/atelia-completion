using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed partial class CodexSubscriptionCompletionClientFactoryTests {
    [Fact]
    public void EnvironmentFactoryRequiresFingerprintWithoutEchoingValues() {
        var reads = new List<string>();
        var error = Assert.Throws<InvalidOperationException>(() =>
            CodexSubscriptionCompletionClientFactory.CreateFromEnvironment(new TrackingFallbackFactory(),
                readEnvironmentVariable: name => { reads.Add(name); return null; }));
        Assert.Equal([CodexSubscriptionCompletionClientFactory.AccountFingerprintEnvironmentVariable], reads);
        Assert.Contains(CodexSubscriptionCompletionClientFactory.AccountFingerprintEnvironmentVariable, error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("configured-originator")]
    public void EnvironmentFactoryCreatesClientWithoutOpeningAuthFile(string? originator) {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "auth.json");
        var fallback = new TrackingFallbackFactory();
        var factory = CodexSubscriptionCompletionClientFactory.CreateFromEnvironment(fallback,
            defaultOriginator: "test-default", productName: "Atelia.Tests",
            readEnvironmentVariable: name => name switch {
                CodexSubscriptionCompletionClientFactory.AccountFingerprintEnvironmentVariable => ExpectedAccountFingerprint,
                CodexSubscriptionCompletionClientFactory.OriginatorEnvironmentVariable => originator,
                CodexSubscriptionCompletionClientFactory.AuthFileEnvironmentVariable => missing,
                _ => throw new InvalidOperationException("Unexpected environment key.")
            });
        using var client = Assert.IsType<OpenAICodexResponsesClient>(factory.Create(CodexConnection()));
        Assert.False(File.Exists(missing));
        Assert.Equal(0, fallback.CallCount);
    }

    [Theory]
    [InlineData(CodexSubscriptionCompletionClientFactory.OriginatorEnvironmentVariable, " ")]
    [InlineData(CodexSubscriptionCompletionClientFactory.AuthFileEnvironmentVariable, " ")]
    [InlineData(CodexSubscriptionCompletionClientFactory.AuthFileEnvironmentVariable, "relative/auth.json")]
    public void EnvironmentFactoryRejectsInvalidOptionalSetting(string key, string value) {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CodexSubscriptionCompletionClientFactory.CreateFromEnvironment(new TrackingFallbackFactory(),
                readEnvironmentVariable: name => name == key ? value
                    : name == CodexSubscriptionCompletionClientFactory.AccountFingerprintEnvironmentVariable
                        ? ExpectedAccountFingerprint : null));
        Assert.Contains(key, error.Message);
    }
}
