using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAICodexResponsesLiveTests {
    private const string EnableEnv =
        "ATELIA_RUN_CODEX_SUBSCRIPTION_LIVE";
    private const string AuthFileEnv =
        "ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE";
    private const string ModelEnv =
        "ATELIA_CODEX_SUBSCRIPTION_LIVE_MODEL";
    private const string OriginatorEnv =
        "ATELIA_CODEX_SUBSCRIPTION_LIVE_ORIGINATOR";

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_ExplicitCodexAuthFile_ReturnsExpectedText() {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableEnv),
                "1",
                StringComparison.Ordinal
            )) {
            return;
        }

        string authFile = Environment.GetEnvironmentVariable(AuthFileEnv)
            ?? throw new InvalidOperationException(
                $"{AuthFileEnv} must name an explicit absolute auth.json path."
            );
        if (!Path.IsPathFullyQualified(authFile)) {
            throw new InvalidOperationException(
                $"{AuthFileEnv} must be an absolute path."
            );
        }
        string model = Environment.GetEnvironmentVariable(ModelEnv)
            ?? "gpt-5.4";
        string originator = Environment.GetEnvironmentVariable(
            OriginatorEnv
        ) ?? "atelia-live-smoke";

        var provider = new CodexCliAuthFileCredentialProvider(authFile);
        CodexSubscriptionCredential snapshot =
            await provider.GetCredentialAsync(CancellationToken.None);
        using var client = new OpenAICodexResponsesClient(
            provider,
            new OpenAICodexResponsesClientOptions {
                ExpectedAccountFingerprint =
                    snapshot.AccountFingerprint,
                Originator = originator,
                ProductName = "Atelia",
                ProductVersion = "live-smoke",
                MaxConcurrentRequests = 1
            }
        );
        var request = new CompletionRequest(
            model,
            new CompletionPromptPrefix(
                "Answer tersely and follow the exact output instruction.",
                CompletionOutputContract.ProviderDefault(
                    ImmutableArray<ToolDefinition>.Empty
                ),
                [new ObservationMessage("Reply with exactly OK.")]
            ),
            tailMessages: []
        );

        CompletionResult result = await client.StreamCompletionAsync(
            request,
            observer: null,
            CancellationToken.None
        );

        Assert.Equal(
            CompletionTerminationKind.Completed,
            result.Termination.Kind
        );
        Assert.Equal("OK", result.Message.GetFlattenedText().Trim());
    }
}
