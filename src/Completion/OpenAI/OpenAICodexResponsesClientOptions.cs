using Atelia.Completion.Abstractions;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAICodexResponsesClientOptions {
    public CompletionReasoningEffort ReasoningEffort { get; init; } =
        CompletionReasoningEffort.ProviderDefault;

    public int MaxConcurrentRequests { get; init; } = 3;

    /// <summary>
    /// Honest, stable harness identity sent in the <c>originator</c> header.
    /// It is construction-time configuration and is not durable dispatch
    /// identity.
    /// </summary>
    public string Originator { get; init; } = "atelia";

    public string ProductName { get; init; } = "Atelia";

    public string? ProductVersion { get; init; }

    /// <summary>
    /// Host-provisioned, non-secret fingerprint for the expected ChatGPT
    /// account. Use <see cref="CodexSubscriptionCredential.AccountFingerprint"/>
    /// to provision it without exposing the raw account id.
    /// </summary>
    public required string ExpectedAccountFingerprint { get; init; }
}
