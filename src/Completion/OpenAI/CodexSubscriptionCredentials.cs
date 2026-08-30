using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Atelia.Completion.OpenAI;

public interface ICodexSubscriptionCredentialProvider {
    ValueTask<CodexSubscriptionCredential> GetCredentialAsync(
        CancellationToken cancellationToken = default
    );
}

[DebuggerDisplay("{ToString(),nq}")]
public sealed class CodexSubscriptionCredential {
    internal const int MaximumSecretUtf8Bytes = 64 * 1024;
    private const int MaximumResidencyUtf8Bytes = 128;
    private const string AccountFingerprintDomain =
        "atelia-chatgpt-account-v1\0";

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _accessToken;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _accountId;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string? _residency;

    private CodexSubscriptionCredential(
        string accessToken,
        string accountId,
        string? residency,
        DateTimeOffset? expiresAt,
        long generation
    ) {
        _accessToken = RequireBoundedNonBlank(
            accessToken,
            MaximumSecretUtf8Bytes,
            nameof(accessToken)
        );
        _accountId = RequireBoundedNonBlank(
            accountId,
            MaximumSecretUtf8Bytes,
            nameof(accountId)
        );
        _residency = string.IsNullOrWhiteSpace(residency)
            ? null
            : RequireBoundedNonBlank(
                residency,
                MaximumResidencyUtf8Bytes,
                nameof(residency)
            );
        if (generation <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "Credential generation must be positive."
            );
        }

        AccountFingerprint = ComputeAccountFingerprint(_accountId);
        ExpiresAt = expiresAt;
        Generation = generation;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string AccessToken => _accessToken;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string AccountId => _accountId;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string? Residency => _residency;

    public string AccountFingerprint { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public long Generation { get; }

    public static CodexSubscriptionCredential Create(
        string accessToken,
        string accountId,
        string? residency,
        DateTimeOffset? expiresAt,
        long stableGeneration
    ) => new(
        accessToken,
        accountId,
        residency,
        expiresAt,
        stableGeneration
    );

    public override string ToString() => nameof(CodexSubscriptionCredential);

    internal bool HasSameEffectiveCredential(
        string accessToken,
        string accountId,
        string? residency,
        DateTimeOffset? expiresAt
    ) => string.Equals(_accessToken, accessToken, StringComparison.Ordinal)
        && string.Equals(_accountId, accountId, StringComparison.Ordinal)
        && string.Equals(_residency, residency, StringComparison.Ordinal)
        && ExpiresAt == expiresAt;

    private static string ComputeAccountFingerprint(string accountId) {
        byte[] bytes = Encoding.UTF8.GetBytes(
            AccountFingerprintDomain + accountId
        );
        try {
            return $"sha256:{Convert.ToHexStringLower(
                SHA256.HashData(bytes)
            )}";
        }
        finally {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string RequireBoundedNonBlank(
        string value,
        int maximumUtf8Bytes,
        string parameterName
    ) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                "A non-empty credential value is required.",
                parameterName
            );
        }
        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes) {
            throw new ArgumentException(
                "A credential value exceeds its size bound.",
                parameterName
            );
        }
        return value;
    }
}

public enum CodexSubscriptionCredentialFailureReason {
    UnsupportedPlatform,
    CredentialPathInvalid,
    AuthStorageUnavailable,
    CredentialStorageUnsafe,
    AuthSnapshotTemporarilyUnreadable,
    AuthSnapshotMalformed,
    UnsupportedAuthMode,
    AuthAccountMissing,
    AuthAccountMismatch,
    AuthAccountChanged,
    AuthOwnerRefreshRequired,
}

public sealed class CodexSubscriptionCredentialException : Exception {
    internal CodexSubscriptionCredentialException(
        CodexSubscriptionCredentialFailureReason reason
    ) : base(GetSafeMessage(reason)) {
        Reason = reason;
    }

    public CodexSubscriptionCredentialFailureReason Reason { get; }

    private static string GetSafeMessage(
        CodexSubscriptionCredentialFailureReason reason
    ) => reason switch {
        CodexSubscriptionCredentialFailureReason.UnsupportedPlatform =>
            "Codex subscription credential file loading is unsupported on this platform.",
        CodexSubscriptionCredentialFailureReason.CredentialPathInvalid =>
            "The Codex subscription credential path is invalid.",
        CodexSubscriptionCredentialFailureReason.AuthStorageUnavailable =>
            "The Codex authentication file is unavailable.",
        CodexSubscriptionCredentialFailureReason.CredentialStorageUnsafe =>
            "The Codex authentication path could not be safely opened as a regular non-symlink file.",
        CodexSubscriptionCredentialFailureReason.AuthSnapshotTemporarilyUnreadable =>
            "The Codex authentication snapshot changed while it was being read.",
        CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed =>
            "The Codex authentication snapshot is malformed or unsupported.",
        CodexSubscriptionCredentialFailureReason.UnsupportedAuthMode =>
            "The Codex authentication snapshot is not a ChatGPT-managed login.",
        CodexSubscriptionCredentialFailureReason.AuthAccountMissing =>
            "The Codex authentication snapshot does not identify a ChatGPT account.",
        CodexSubscriptionCredentialFailureReason.AuthAccountMismatch =>
            "The Codex authentication snapshot contains conflicting ChatGPT account identities.",
        CodexSubscriptionCredentialFailureReason.AuthAccountChanged =>
            "The Codex authentication snapshot does not match the expected ChatGPT account.",
        CodexSubscriptionCredentialFailureReason.AuthOwnerRefreshRequired =>
            "The Codex authentication owner must refresh or replace the expired login.",
        _ => "The Codex subscription credential is unavailable."
    };
}
