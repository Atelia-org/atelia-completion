using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;

namespace Atelia.Completion.OpenAI;

/// <summary>
/// Adds the pinned ChatGPT Codex subscription connection kind while preserving
/// the existing factory behavior for every other Completion connection.
/// </summary>
public sealed class CodexSubscriptionCompletionClientFactory
    : ICompletionClientFactory {
    public const string ConnectionKind =
        ChatGptCodexResponsesProfile.ConnectionKind;
    public const string CompletionSurfaceId =
        ChatGptCodexResponsesProfile.CompletionSurfaceId;
    public const string CanonicalBaseAddress =
        ChatGptCodexResponsesProfile.CanonicalBaseAddressText;

    private readonly ICodexSubscriptionCredentialProvider _credentialProvider;
    private readonly ICompletionClientFactory _fallback;
    private readonly OpenAICodexResponsesClientOptions _options;

    public CodexSubscriptionCompletionClientFactory(
        ICodexSubscriptionCredentialProvider credentialProvider,
        string expectedAccountFingerprint,
        string originator = "atelia",
        ICompletionClientFactory? fallback = null,
        int maxConcurrentRequests = 3,
        string productName = "Atelia",
        string? productVersion = null
    ) {
        _credentialProvider = credentialProvider
            ?? throw new ArgumentNullException(nameof(credentialProvider));
        ValidateFactoryOptions(
            expectedAccountFingerprint,
            originator,
            maxConcurrentRequests,
            productName,
            productVersion
        );
        _fallback = fallback ?? new DefaultCompletionClientFactory();
        _options = new OpenAICodexResponsesClientOptions {
            ExpectedAccountFingerprint = expectedAccountFingerprint,
            Originator = originator,
            MaxConcurrentRequests = maxConcurrentRequests,
            ProductName = productName,
            ProductVersion = productVersion
        };
    }

    public ICompletionClient Create(CompletionConnectionConfig connection) {
        ArgumentNullException.ThrowIfNull(connection);

        if (!string.Equals(
                connection.Kind,
                ConnectionKind,
                StringComparison.Ordinal
            )) {
            if (IsCodexKindPseudoVariant(connection.Kind)) {
                throw new InvalidOperationException(
                    $"Completion connection '{connection.Id}' must use exact kind "
                    + $"'{ConnectionKind}'."
                );
            }
            return _fallback.Create(connection);
        }

        ValidateCodexConnection(connection);
        return new OpenAICodexResponsesClient(
            _credentialProvider,
            new OpenAICodexResponsesClientOptions {
                ReasoningEffort = connection.ReasoningEffort,
                ExpectedAccountFingerprint =
                    _options.ExpectedAccountFingerprint,
                Originator = _options.Originator,
                MaxConcurrentRequests = _options.MaxConcurrentRequests,
                ProductName = _options.ProductName,
                ProductVersion = _options.ProductVersion
            }
        );
    }

    private static bool IsCodexKindPseudoVariant(string? value)
        => value is not null
            && string.Equals(
                value.Trim(),
                ConnectionKind,
                StringComparison.OrdinalIgnoreCase
            );

    private static void ValidateCodexConnection(
        CompletionConnectionConfig connection
    ) {
        if (!string.Equals(
                connection.CompletionSurfaceId,
                CompletionSurfaceId,
                StringComparison.Ordinal
            )) {
            throw new InvalidOperationException(
                $"Completion connection '{connection.Id}' must use exact "
                + $"completionSurfaceId '{CompletionSurfaceId}'."
            );
        }
        if (!string.Equals(
                connection.BaseAddress,
                CanonicalBaseAddress,
                StringComparison.Ordinal
            )) {
            throw new InvalidOperationException(
                $"Completion connection '{connection.Id}' must resolve to exact "
                + $"baseAddress '{CanonicalBaseAddress}'."
            );
        }
        if (connection.ApiKey is not null
            || connection.ApiKeyEnv is not null) {
            throw new InvalidOperationException(
                $"Completion connection '{connection.Id}' must not configure "
                + "apiKey or apiKeyEnv for Codex subscription authentication."
            );
        }
        if (connection.MaxTokens is not null) {
            throw new InvalidOperationException(
                $"Completion connection '{connection.Id}' must not configure "
                + "MaxTokens for the Codex subscription surface."
            );
        }
        if (connection.AnthropicPromptCacheTtl
                is not AnthropicPromptCacheTtl.ProviderDefault) {
            throw new InvalidOperationException(
                $"Completion connection '{connection.Id}' must not configure "
                + "anthropicPromptCacheTtl for the Codex subscription surface."
            );
        }
        if (!Enum.IsDefined(connection.ReasoningEffort)) {
            throw new InvalidOperationException(
                $"Completion connection '{connection.Id}' has unsupported "
                + $"reasoningEffort value '{connection.ReasoningEffort}'."
            );
        }
    }

    private static void ValidateFactoryOptions(
        string expectedAccountFingerprint,
        string originator,
        int maxConcurrentRequests,
        string productName,
        string? productVersion
    ) {
        if (!IsLowerSha256(expectedAccountFingerprint)) {
            throw new ArgumentException(
                "Expected account fingerprint must be a lowercase sha256 fingerprint.",
                nameof(expectedAccountFingerprint)
            );
        }
        if (!IsValidOriginator(originator)) {
            throw new ArgumentException(
                "Originator must match ^[a-z][a-z0-9._-]{0,63}$.",
                nameof(originator)
            );
        }
        if (maxConcurrentRequests is < 1 or > 8) {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentRequests),
                maxConcurrentRequests,
                "Max concurrent requests must be between 1 and 8."
            );
        }
        if (!IsSafeProductToken(productName, 64)) {
            throw new ArgumentException(
                "Product name must be printable ASCII without whitespace or HTTP separators.",
                nameof(productName)
            );
        }
        if (productVersion is not null
            && !IsSafeProductToken(productVersion, 64)) {
            throw new ArgumentException(
                "Product version must be printable ASCII without whitespace or HTTP separators.",
                nameof(productVersion)
            );
        }
    }

    private static bool IsLowerSha256(string? value)
        => value is { Length: 71 }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && value.AsSpan(7).IndexOfAnyExcept(
                "0123456789abcdef"
            ) < 0;

    private static bool IsValidOriginator(string? value) {
        if (value is not { Length: >= 1 and <= 64 }
            || value[0] is < 'a' or > 'z') {
            return false;
        }
        return value.AsSpan(1).IndexOfAnyExcept(
            "abcdefghijklmnopqrstuvwxyz0123456789._-"
        ) < 0;
    }

    private static bool IsSafeProductToken(
        string? value,
        int maximumLength
    ) {
        if (value is not { Length: > 0 }
            || value.Length > maximumLength) {
            return false;
        }
        foreach (char c in value) {
            if (c is < '!' or > '~'
                || "()<>@,;:\\\"/[]?={}".Contains(c)) {
                return false;
            }
        }
        return true;
    }
}
