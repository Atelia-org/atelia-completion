using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Completion.OpenAI;

[DebuggerDisplay("{ToString(),nq}")]
public sealed class CodexCliAuthFileCredentialProvider
    : ICodexSubscriptionCredentialProvider {
    internal const int MaximumAuthFileBytes = 128 * 1024;
    internal const int MaximumSecretUtf8Bytes =
        CodexSubscriptionCredential.MaximumSecretUtf8Bytes;

    private const int MaximumJsonDepth = 16;
    private const int MaximumPropertyNameUtf8Bytes = 1024;
    private const string AuthFileName = "auth.json";
    private const string CodexHomeEnvironmentVariable = "CODEX_HOME";
    private const string ChatGptAuthMode = "chatgpt";
    private const string OpenAiAuthClaim = "https://api.openai.com/auth";

    private readonly SemaphoreSlim _generationGate = new(1, 1);
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _authFilePath;
    private readonly TimeProvider _timeProvider;
    private readonly Action? _betweenSnapshotReads;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private CodexSubscriptionCredential? _lastCredential;
    private long _lastGeneration;

    public CodexCliAuthFileCredentialProvider()
        : this(ResolveDefaultAuthFilePath(), TimeProvider.System) { }

    public CodexCliAuthFileCredentialProvider(string authFilePath)
        : this(authFilePath, TimeProvider.System) { }

    internal CodexCliAuthFileCredentialProvider(
        string authFilePath,
        TimeProvider timeProvider
    ) : this(authFilePath, timeProvider, betweenSnapshotReads: null) { }

    internal CodexCliAuthFileCredentialProvider(
        string authFilePath,
        TimeProvider timeProvider,
        Action? betweenSnapshotReads
    ) {
        _authFilePath = NormalizeExplicitAuthFilePath(authFilePath);
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        _betweenSnapshotReads = betweenSnapshotReads;
    }

    public async ValueTask<CodexSubscriptionCredential> GetCredentialAsync(
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason.UnsupportedPlatform
            );
        }

        await _generationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try {
            // Serialize the read with generation publication. Otherwise a
            // pre-rotation read can arrive after a post-rotation read and make
            // the provider publish a stale snapshot as the newest generation.
            cancellationToken.ThrowIfCancellationRequested();
            ParsedCredential parsed = ReadCoherentCredential(
                cancellationToken
            );
            if (parsed.ExpiresAt is { } expiresAt
                && expiresAt <= _timeProvider.GetUtcNow()) {
                throw Failure(
                    CodexSubscriptionCredentialFailureReason
                        .AuthOwnerRefreshRequired
                );
            }

            if (_lastCredential is not null
                && _lastCredential.HasSameEffectiveCredential(
                    parsed.AccessToken,
                    parsed.AccountId,
                    parsed.Residency,
                    parsed.ExpiresAt
                )) {
                return _lastCredential;
            }

            long generation = checked(_lastGeneration + 1);
            CodexSubscriptionCredential credential =
                CodexSubscriptionCredential.Create(
                    parsed.AccessToken,
                    parsed.AccountId,
                    parsed.Residency,
                    parsed.ExpiresAt,
                    generation
                );
            _lastCredential = credential;
            _lastGeneration = generation;
            return credential;
        }
        finally {
            _generationGate.Release();
        }
    }

    public override string ToString()
        => nameof(CodexCliAuthFileCredentialProvider);

    [SupportedOSPlatform("linux")]
    private ParsedCredential ReadCoherentCredential(
        CancellationToken cancellationToken
    ) {
        bool changedDuringRead = false;
        for (int attempt = 0; attempt < 2; attempt++) {
            byte[]? bytes = null;
            try {
                bytes = ReadOneSnapshot(cancellationToken);
                return ParseAuthDocument(bytes);
            }
            catch (SnapshotChangedException) when (attempt == 0) {
                changedDuringRead = true;
            }
            catch (JsonException) when (attempt == 0) {
                changedDuringRead = true;
            }
            catch (SnapshotChangedException) {
                throw Failure(
                    CodexSubscriptionCredentialFailureReason
                        .AuthSnapshotTemporarilyUnreadable
                );
            }
            catch (JsonException) {
                throw Failure(
                    CodexSubscriptionCredentialFailureReason
                        .AuthSnapshotMalformed
                );
            }
            finally {
                if (bytes is not null) {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        throw Failure(
            changedDuringRead
                ? CodexSubscriptionCredentialFailureReason
                    .AuthSnapshotTemporarilyUnreadable
                : CodexSubscriptionCredentialFailureReason
                    .AuthSnapshotMalformed
        );
    }

    [SupportedOSPlatform("linux")]
    private byte[] ReadOneSnapshot(CancellationToken cancellationToken) {
        string? directoryPath = Path.GetDirectoryName(_authFilePath);
        string fileName = Path.GetFileName(_authFilePath);
        if (string.IsNullOrWhiteSpace(directoryPath)
            || string.IsNullOrWhiteSpace(fileName)) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason
                    .CredentialPathInvalid
            );
        }

        using SafeFileHandle directory = LinuxOpen.OpenDirectoryChain(
            directoryPath
        );
        LinuxOpen.ValidateDirectoryType(directory);
        cancellationToken.ThrowIfCancellationRequested();

        using SafeFileHandle file = LinuxOpen.OpenFile(directory, fileName);
        LinuxOpen.ValidateRegularFile(file);

        long length;
        try {
            length = RandomAccess.GetLength(file);
        }
        catch (Exception exception) when (IsNonFatalIo(exception)) {
            throw new SnapshotChangedException();
        }
        if (length is < 1 or > MaximumAuthFileBytes) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason
                    .AuthSnapshotMalformed
            );
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        byte[] comparison = GC.AllocateUninitializedArray<byte>(
            checked((int)length)
        );
        try {
            ReadExactly(file, bytes, cancellationToken);
            _betweenSnapshotReads?.Invoke();
            ReadExactly(file, comparison, cancellationToken);

            Span<byte> extra = stackalloc byte[1];
            long finalLength;
            int extraRead;
            try {
                finalLength = RandomAccess.GetLength(file);
                extraRead = RandomAccess.Read(file, extra, length);
            }
            catch (Exception exception) when (IsNonFatalIo(exception)) {
                throw new SnapshotChangedException();
            }
            if (finalLength != length
                || extraRead != 0
                || !CryptographicOperations.FixedTimeEquals(
                    bytes,
                    comparison
                )) {
                throw new SnapshotChangedException();
            }

            return bytes;
        }
        catch {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
        finally {
            CryptographicOperations.ZeroMemory(comparison);
        }
    }

    private static void ReadExactly(
        SafeFileHandle file,
        Span<byte> destination,
        CancellationToken cancellationToken
    ) {
        int total = 0;
        while (total < destination.Length) {
            cancellationToken.ThrowIfCancellationRequested();
            int read;
            try {
                read = RandomAccess.Read(
                    file,
                    destination[total..],
                    total
                );
            }
            catch (Exception exception) when (IsNonFatalIo(exception)) {
                throw new SnapshotChangedException();
            }
            if (read == 0) {
                throw new SnapshotChangedException();
            }
            total = checked(total + read);
        }
    }

    private static ParsedCredential ParseAuthDocument(
        ReadOnlySpan<byte> bytes
    ) {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth
        });

        RequireRead(ref reader, JsonTokenType.StartObject);
        var seen = NewPropertySet();
        string? authMode = null;
        ParsedTokens? tokens = null;

        while (ReadProperty(ref reader, seen, out string property)) {
            RequireCanonicalKnownProperty(
                property,
                ["auth_mode", "tokens"]
            );
            RequireReadValue(ref reader);
            switch (property) {
                case "auth_mode":
                    authMode = ReadBoundedString(
                        ref reader,
                        maximumUtf8Bytes: 32
                    );
                    break;
                case "tokens":
                    tokens = ParseTokens(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        if (reader.Read()) {
            throw Malformed();
        }
        if (!string.Equals(authMode, ChatGptAuthMode, StringComparison.Ordinal)) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason.UnsupportedAuthMode
            );
        }
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken)) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason
                    .AuthSnapshotMalformed
            );
        }
        if (string.IsNullOrWhiteSpace(tokens.AccountId)) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason.AuthAccountMissing
            );
        }

        JwtMetadata jwt = ParseAccessToken(tokens.AccessToken);
        if (jwt.AccountId is not null
            && !string.Equals(
                jwt.AccountId,
                tokens.AccountId,
                StringComparison.Ordinal
            )) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason.AuthAccountMismatch
            );
        }

        return new ParsedCredential(
            tokens.AccessToken,
            tokens.AccountId,
            jwt.Residency,
            jwt.ExpiresAt
        );
    }

    private static ParsedTokens ParseTokens(ref Utf8JsonReader reader) {
        RequireToken(reader.TokenType, JsonTokenType.StartObject);
        var seen = NewPropertySet();
        string? accessToken = null;
        string? accountId = null;

        while (ReadProperty(ref reader, seen, out string property)) {
            RequireCanonicalKnownProperty(
                property,
                ["access_token", "account_id", "id_token", "refresh_token"]
            );
            RequireReadValue(ref reader);
            switch (property) {
                case "access_token":
                    accessToken = ReadBoundedString(
                        ref reader,
                        MaximumSecretUtf8Bytes
                    );
                    break;
                case "account_id":
                    accountId = reader.TokenType is JsonTokenType.Null
                        ? null
                        : ReadBoundedString(
                            ref reader,
                            MaximumSecretUtf8Bytes
                        );
                    break;
                default:
                    // id_token and refresh_token are deliberately never
                    // materialized into managed strings.
                    reader.Skip();
                    break;
            }
        }
        return new ParsedTokens(accessToken, accountId);
    }

    private static JwtMetadata ParseAccessToken(string accessToken) {
        int firstDot = accessToken.IndexOf('.');
        int secondDot = firstDot < 0
            ? -1
            : accessToken.IndexOf('.', firstDot + 1);
        if (firstDot <= 0
            || secondDot <= firstDot + 1
            || secondDot >= accessToken.Length - 1
            || accessToken.IndexOf('.', secondDot + 1) >= 0) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed
            );
        }

        byte[] payload;
        try {
            payload = Base64Url.DecodeFromChars(
                accessToken.AsSpan(firstDot + 1, secondDot - firstDot - 1)
            );
        }
        catch (Exception exception) when (
            exception is FormatException
                or ArgumentException
                or OverflowException) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed
            );
        }
        if (payload.Length > MaximumSecretUtf8Bytes) {
            CryptographicOperations.ZeroMemory(payload);
            throw Failure(
                CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed
            );
        }

        try {
            return ParseJwtPayload(payload);
        }
        catch (JsonException) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed
            );
        }
        finally {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static JwtMetadata ParseJwtPayload(ReadOnlySpan<byte> payload) {
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumJsonDepth
        });
        RequireRead(ref reader, JsonTokenType.StartObject);
        var seen = NewPropertySet();
        DateTimeOffset? expiresAt = null;
        string? accountId = null;

        while (ReadProperty(ref reader, seen, out string property)) {
            RequireCanonicalKnownProperty(property, ["exp", OpenAiAuthClaim]);
            RequireReadValue(ref reader);
            switch (property) {
                case "exp":
                    if (reader.TokenType is not JsonTokenType.Number
                        || !reader.TryGetInt64(out long seconds)) {
                        throw Malformed();
                    }
                    try {
                        expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    }
                    catch (ArgumentOutOfRangeException) {
                        throw Failure(
                            CodexSubscriptionCredentialFailureReason
                                .AuthSnapshotMalformed
                        );
                    }
                    break;
                case OpenAiAuthClaim:
                    accountId = ParseOpenAiAuthClaims(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        if (reader.Read()) {
            throw Malformed();
        }

        // The pinned Codex auth format derives the residency header from
        // managed configuration, not from auth.json. WP-1 therefore carries no
        // inferred residency value.
        return new JwtMetadata(expiresAt, accountId, Residency: null);
    }

    private static string? ParseOpenAiAuthClaims(
        ref Utf8JsonReader reader
    ) {
        if (reader.TokenType is JsonTokenType.Null) {
            return null;
        }
        RequireToken(reader.TokenType, JsonTokenType.StartObject);
        var seen = NewPropertySet();
        string? accountId = null;
        while (ReadProperty(ref reader, seen, out string property)) {
            RequireCanonicalKnownProperty(property, ["chatgpt_account_id"]);
            RequireReadValue(ref reader);
            if (property == "chatgpt_account_id") {
                accountId = reader.TokenType is JsonTokenType.Null
                    ? null
                    : ReadBoundedString(
                        ref reader,
                        MaximumSecretUtf8Bytes
                    );
            }
            else {
                reader.Skip();
            }
        }
        return string.IsNullOrWhiteSpace(accountId) ? null : accountId;
    }

    private static HashSet<string> NewPropertySet()
        => new(StringComparer.OrdinalIgnoreCase);

    private static bool ReadProperty(
        ref Utf8JsonReader reader,
        HashSet<string> seen,
        out string property
    ) {
        if (!reader.Read()) {
            throw new JsonException();
        }
        if (reader.TokenType is JsonTokenType.EndObject) {
            property = string.Empty;
            return false;
        }
        RequireToken(reader.TokenType, JsonTokenType.PropertyName);
        property = reader.GetString() ?? throw Malformed();
        if (Encoding.UTF8.GetByteCount(property)
                > MaximumPropertyNameUtf8Bytes
            || !seen.Add(property)) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed
            );
        }
        return true;
    }

    private static void RequireCanonicalKnownProperty(
        string property,
        IReadOnlyList<string> known
    ) {
        foreach (string canonical in known) {
            if (string.Equals(
                    property,
                    canonical,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    property,
                    canonical,
                    StringComparison.Ordinal
                )) {
                throw Failure(
                    CodexSubscriptionCredentialFailureReason
                        .AuthSnapshotMalformed
                );
            }
        }
    }

    private static string ReadBoundedString(
        ref Utf8JsonReader reader,
        int maximumUtf8Bytes
    ) {
        RequireToken(reader.TokenType, JsonTokenType.String);
        string value = reader.GetString() ?? throw Malformed();
        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed
            );
        }
        return value;
    }

    private static void RequireRead(
        ref Utf8JsonReader reader,
        JsonTokenType expected
    ) {
        if (!reader.Read()) {
            throw new JsonException();
        }
        RequireToken(reader.TokenType, expected);
    }

    private static void RequireReadValue(ref Utf8JsonReader reader) {
        if (!reader.Read()) {
            throw new JsonException();
        }
    }

    private static void RequireToken(
        JsonTokenType actual,
        JsonTokenType expected
    ) {
        if (actual != expected) {
            throw Malformed();
        }
    }

    private static string ResolveDefaultAuthFilePath() {
        string? configured = Environment.GetEnvironmentVariable(
            CodexHomeEnvironmentVariable
        );
        string codexHome;
        if (configured is not null) {
            if (string.IsNullOrWhiteSpace(configured)
                || !Path.IsPathFullyQualified(configured)) {
                throw Failure(
                    CodexSubscriptionCredentialFailureReason
                        .CredentialPathInvalid
                );
            }
            codexHome = Path.GetFullPath(configured);
        }
        else {
            string userProfile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile
            );
            if (string.IsNullOrWhiteSpace(userProfile)
                || !Path.IsPathFullyQualified(userProfile)) {
                throw Failure(
                    CodexSubscriptionCredentialFailureReason
                        .CredentialPathInvalid
                );
            }
            codexHome = Path.Combine(
                Path.GetFullPath(userProfile),
                ".codex"
            );
        }
        return Path.Combine(codexHome, AuthFileName);
    }

    private static string NormalizeExplicitAuthFilePath(string path) {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason
                    .CredentialPathInvalid
            );
        }
        try {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException) {
            throw Failure(
                CodexSubscriptionCredentialFailureReason
                    .CredentialPathInvalid
            );
        }
    }

    private static CodexSubscriptionCredentialException Failure(
        CodexSubscriptionCredentialFailureReason reason
    ) => new(reason);

    private static CodexSubscriptionCredentialException Malformed()
        => Failure(
            CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed
        );

    private static bool IsNonFatalIo(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException;

    private sealed class SnapshotChangedException : Exception;

    private sealed record ParsedTokens(
        string? AccessToken,
        string? AccountId
    );

    private sealed record ParsedCredential(
        string AccessToken,
        string AccountId,
        string? Residency,
        DateTimeOffset? ExpiresAt
    );

    private sealed record JwtMetadata(
        DateTimeOffset? ExpiresAt,
        string? AccountId,
        string? Residency
    );

    [SupportedOSPlatform("linux")]
    private static class LinuxOpen {
        private const int OpenReadOnly = 0;
        private const int OpenNonBlocking = 0x800;
        private const int OpenDirectoryFlag = 0x10000;
        private const int OpenNoFollow = 0x20000;
        private const int OpenCloseOnExec = 0x80000;

        private const int ErrorNoEntry = 2;
        private const uint LinuxFileTypeMask = 0xF000;
        private const uint LinuxDirectoryType = 0x4000;
        private const uint LinuxRegularFileType = 0x8000;

        public static SafeFileHandle OpenDirectoryChain(string path) {
            string canonical = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path)
            );
            string root = Path.GetPathRoot(canonical)
                ?? throw Failure(
                    CodexSubscriptionCredentialFailureReason
                        .CredentialPathInvalid
                );
            SafeFileHandle? current = OpenAbsoluteDirectory(root);
            try {
                foreach (string component in canonical[root.Length..].Split(
                             Path.DirectorySeparatorChar,
                             StringSplitOptions.RemoveEmptyEntries)) {
                    SafeFileHandle next = OpenDirectoryAt(
                        current,
                        component
                    );
                    current.Dispose();
                    current = next;
                }

                SafeFileHandle result = current;
                current = null;
                return result;
            }
            finally {
                current?.Dispose();
            }
        }

        public static SafeFileHandle OpenFile(
            SafeFileHandle directory,
            string fileName
        ) {
            bool addedRef = false;
            try {
                directory.DangerousAddRef(ref addedRef);
                int descriptor = OpenAt(
                    checked((int)directory.DangerousGetHandle()),
                    fileName,
                    OpenReadOnly
                        | OpenNonBlocking
                        | OpenNoFollow
                        | OpenCloseOnExec
                );
                return OwnOpenedDescriptor(descriptor);
            }
            finally {
                if (addedRef) {
                    directory.DangerousRelease();
                }
            }
        }

        private static SafeFileHandle OpenAbsoluteDirectory(string path) {
            int descriptor = Open(
                path,
                OpenReadOnly
                    | OpenDirectoryFlag
                    | OpenNoFollow
                    | OpenCloseOnExec
            );
            return OwnOpenedDescriptor(descriptor);
        }

        private static SafeFileHandle OpenDirectoryAt(
            SafeFileHandle directory,
            string component
        ) {
            bool addedRef = false;
            try {
                directory.DangerousAddRef(ref addedRef);
                int descriptor = OpenAt(
                    checked((int)directory.DangerousGetHandle()),
                    component,
                    OpenReadOnly
                        | OpenDirectoryFlag
                        | OpenNoFollow
                        | OpenCloseOnExec
                );
                return OwnOpenedDescriptor(descriptor);
            }
            finally {
                if (addedRef) {
                    directory.DangerousRelease();
                }
            }
        }

        public static void ValidateDirectoryType(
            SafeFileHandle handle
        ) {
            if ((ReadMode(handle) & LinuxFileTypeMask) != LinuxDirectoryType) {
                throw Failure(
                    CodexSubscriptionCredentialFailureReason
                        .CredentialStorageUnsafe
                );
            }
        }

        public static void ValidateRegularFile(SafeFileHandle handle) {
            // Read authority comes from the successful OS open. This
            // read-only consumer deliberately does not infer authority or
            // confidentiality from uid/gid or Unix permission bits.
            if ((ReadMode(handle) & LinuxFileTypeMask) != LinuxRegularFileType) {
                throw Failure(
                    CodexSubscriptionCredentialFailureReason
                        .CredentialStorageUnsafe
                );
            }
        }

        private static SafeFileHandle OwnOpenedDescriptor(int descriptor) {
            if (descriptor < 0) {
                int error = Marshal.GetLastPInvokeError();
                throw Failure(
                    error == ErrorNoEntry
                        ? CodexSubscriptionCredentialFailureReason
                            .AuthStorageUnavailable
                        : CodexSubscriptionCredentialFailureReason
                            .CredentialStorageUnsafe
                );
            }
            return new SafeFileHandle(
                new IntPtr(descriptor),
                ownsHandle: true
            );
        }

        private static uint ReadMode(SafeFileHandle handle) {
            bool addedRef = false;
            IntPtr buffer = Marshal.AllocHGlobal(256);
            try {
                handle.DangerousAddRef(ref addedRef);
                int descriptor = checked((int)handle.DangerousGetHandle());
                if (FStat(descriptor, buffer) != 0) {
                    throw Failure(
                        CodexSubscriptionCredentialFailureReason
                            .AuthSnapshotTemporarilyUnreadable
                    );
                }

                int modeOffset = RuntimeInformation.ProcessArchitecture switch {
                    Architecture.X64 => 24,
                    Architecture.Arm64 => 16,
                    _ => throw Failure(
                        CodexSubscriptionCredentialFailureReason
                            .UnsupportedPlatform
                    )
                };
                uint mode = unchecked((uint)Marshal.ReadInt32(
                    buffer,
                    modeOffset
                ));
                return mode;
            }
            finally {
                if (addedRef) {
                    handle.DangerousRelease();
                }
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAt(
            int directoryDescriptor,
            string path,
            int flags
        );

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int FStat(int descriptor, IntPtr value);

    }
}
