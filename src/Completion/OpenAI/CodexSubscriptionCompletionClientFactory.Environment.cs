namespace Atelia.Completion.OpenAI;

public sealed partial class CodexSubscriptionCompletionClientFactory {
    public const string AccountFingerprintEnvironmentVariable = "ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT";
    public const string OriginatorEnvironmentVariable = "ATELIA_CODEX_SUBSCRIPTION_ORIGINATOR";
    public const string AuthFileEnvironmentVariable = "ATELIA_CODEX_SUBSCRIPTION_AUTH_FILE";

    /// <summary>Reads subscription configuration without reading credentials or making requests.</summary>
    public static CodexSubscriptionCompletionClientFactory CreateFromEnvironment(
        ICompletionClientFactory fallback,
        string defaultOriginator = "atelia",
        string productName = "Atelia",
        Func<string, string?>? readEnvironmentVariable = null
    ) {
        ArgumentNullException.ThrowIfNull(fallback);
        readEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        string fingerprint = RequireEnvironmentValue(
            readEnvironmentVariable(AccountFingerprintEnvironmentVariable), AccountFingerprintEnvironmentVariable);
        string? configuredOriginator = readEnvironmentVariable(OriginatorEnvironmentVariable);
        string originator = configuredOriginator is null ? defaultOriginator
            : RequireEnvironmentValue(configuredOriginator, OriginatorEnvironmentVariable);
        string? configuredAuthFile = readEnvironmentVariable(AuthFileEnvironmentVariable);
        ICodexSubscriptionCredentialProvider credentials;
        if (configuredAuthFile is null) {
            credentials = new CodexCliAuthFileCredentialProvider();
        }
        else {
            string authFile = RequireEnvironmentValue(configuredAuthFile, AuthFileEnvironmentVariable);
            if (!Path.IsPathFullyQualified(authFile)) {
                throw new InvalidOperationException($"{AuthFileEnvironmentVariable} must contain an absolute path when configured.");
            }
            credentials = new CodexCliAuthFileCredentialProvider(authFile);
        }
        return new CodexSubscriptionCompletionClientFactory(
            credentials, fingerprint, originator, fallback, productName: productName);
    }

    private static string RequireEnvironmentValue(string? value, string name) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new InvalidOperationException($"{name} is required and must not be blank when using an openai-codex-responses connection.");
        }
        return value;
    }
}
