using System.Buffers.Text;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

[Collection(CodexEnvironmentCollection.Name)]
[SupportedOSPlatform("linux")]
public sealed class CodexCliAuthFileCredentialProviderTests {
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        25,
        12,
        0,
        0,
        TimeSpan.Zero
    );

    [Fact]
    public async Task GetCredentialAsync_ReadsCoherentOpaqueSnapshot() {
        using var fixture = new AuthFixture();
        DateTimeOffset expiresAt = Now.AddHours(1);
        string accessToken = CreateAccessToken(expiresAt, "account-1");
        fixture.WriteAuth(accessToken, "account-1");

        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now)
        );
        CodexSubscriptionCredential credential =
            await provider.GetCredentialAsync();

        Assert.Equal(accessToken, credential.AccessToken);
        Assert.Equal("account-1", credential.AccountId);
        Assert.Null(credential.Residency);
        Assert.Equal(expiresAt, credential.ExpiresAt);
        Assert.Equal(1, credential.Generation);
        Assert.Equal(
            ComputeExpectedFingerprint("account-1"),
            credential.AccountFingerprint
        );
        Assert.Equal(
            nameof(CodexSubscriptionCredential),
            credential.ToString()
        );

        string serialized = JsonSerializer.Serialize(credential);
        Assert.DoesNotContain(accessToken, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("account-1", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthFixture.RefreshCanary, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthFixture.IdTokenCanary, serialized, StringComparison.Ordinal);
        Assert.Contains(credential.AccountFingerprint, serialized, StringComparison.Ordinal);

        foreach (string propertyName in new[] {
            "AccessToken", "AccountId", "Residency"
        }) {
            PropertyInfo property = typeof(CodexSubscriptionCredential)
                .GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.NonPublic
                )!;
            DebuggerBrowsableAttribute attribute = Assert.Single(
                property.GetCustomAttributes<DebuggerBrowsableAttribute>()
            );
            Assert.Equal(DebuggerBrowsableState.Never, attribute.State);
        }
    }

    [Fact]
    public async Task GetCredentialAsync_RetriesSameLengthInPlaceRewrite() {
        using var fixture = new AuthFixture();
        DateTimeOffset expiresAt = Now.AddHours(1);
        string firstToken = CreateAccessToken(
            expiresAt,
            "account-rewrite",
            "one"
        );
        string secondToken = CreateAccessToken(
            expiresAt,
            "account-rewrite",
            "two"
        );
        Assert.Equal(firstToken.Length, secondToken.Length);
        fixture.WriteAuth(firstToken, "account-rewrite");
        long originalLength = new FileInfo(fixture.AuthFilePath).Length;
        int hookCalls = 0;
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now),
            betweenSnapshotReads: () => {
                if (Interlocked.Increment(ref hookCalls) == 1) {
                    fixture.WriteAuth(secondToken, "account-rewrite");
                    Assert.Equal(
                        originalLength,
                        new FileInfo(fixture.AuthFilePath).Length
                    );
                }
            }
        );

        CodexSubscriptionCredential credential =
            await provider.GetCredentialAsync();

        Assert.Equal(secondToken, credential.AccessToken);
        Assert.Equal(1, credential.Generation);
        Assert.Equal(2, hookCalls);
    }

    [Fact]
    public async Task GetCredentialAsync_UsesStableEffectiveGeneration() {
        using var fixture = new AuthFixture();
        DateTimeOffset expiresAt = Now.AddHours(1);
        string firstToken = CreateAccessToken(expiresAt, "account-1", "one");
        fixture.WriteAuth(firstToken, "account-1", lastRefresh: "first");
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now)
        );

        CodexSubscriptionCredential first = await provider.GetCredentialAsync();
        CodexSubscriptionCredential same = await provider.GetCredentialAsync();
        fixture.WriteAuth(firstToken, "account-1", lastRefresh: "second");
        CodexSubscriptionCredential metadataOnly =
            await provider.GetCredentialAsync();

        Assert.Same(first, same);
        Assert.Same(first, metadataOnly);
        Assert.Equal(1, metadataOnly.Generation);

        string secondToken = CreateAccessToken(expiresAt, "account-1", "two");
        fixture.WriteAuth(secondToken, "account-1");
        CodexSubscriptionCredential rotated =
            await provider.GetCredentialAsync();

        Assert.Equal(2, rotated.Generation);
        Assert.Equal(secondToken, rotated.AccessToken);
        Assert.Equal(firstToken, first.AccessToken);
        Assert.Equal(first.AccountFingerprint, rotated.AccountFingerprint);

        string otherAccountToken = CreateAccessToken(expiresAt, "account-2");
        fixture.WriteAuth(otherAccountToken, "account-2");
        CodexSubscriptionCredential changedAccount =
            await provider.GetCredentialAsync();

        Assert.Equal(3, changedAccount.Generation);
        Assert.NotEqual(
            first.AccountFingerprint,
            changedAccount.AccountFingerprint
        );
    }

    [Fact]
    public async Task GetCredentialAsync_ConcurrentReadsShareOneGeneration() {
        using var fixture = new AuthFixture();
        string token = CreateAccessToken(Now.AddHours(1), "account-concurrent");
        fixture.WriteAuth(token, "account-concurrent");
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now)
        );

        CodexSubscriptionCredential[] credentials = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(async _ =>
                await provider.GetCredentialAsync()
            )
        );

        Assert.All(credentials, credential => Assert.Equal(1, credential.Generation));
        Assert.All(credentials, credential => Assert.Same(credentials[0], credential));
    }

    [Fact]
    public async Task GetCredentialAsync_CancellationWhileWaitingForGenerationGatePreservesCallerToken() {
        using var fixture = new AuthFixture();
        string token = CreateAccessToken(Now.AddHours(1), "account-gate");
        fixture.WriteAuth(token, "account-gate");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int hookCalls = 0;
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now),
            betweenSnapshotReads: () => {
                if (Interlocked.Increment(ref hookCalls) == 1) {
                    entered.Set();
                    release.Wait();
                }
            }
        );
        Task<CodexSubscriptionCredential> first = Task.Run(async () =>
            await provider.GetCredentialAsync()
        );
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

            using var caller = new CancellationTokenSource();
            ValueTask<CodexSubscriptionCredential> waiting =
                provider.GetCredentialAsync(caller.Token);
            caller.Cancel();

            OperationCanceledException exception = await Assert.ThrowsAnyAsync<
                OperationCanceledException
            >(() => waiting.AsTask());
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        finally {
            release.Set();
        }
        _ = await first;
    }

    [Fact]
    public async Task GetCredentialAsync_RejectsExpiredTokenBeforeReturningIt() {
        using var fixture = new AuthFixture();
        string token = CreateAccessToken(Now, "account-expired");
        fixture.WriteAuth(token, "account-expired");
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now)
        );

        CodexSubscriptionCredentialException exception =
            await Assert.ThrowsAsync<CodexSubscriptionCredentialException>(
                () => provider.GetCredentialAsync().AsTask()
            );

        Assert.Equal(
            CodexSubscriptionCredentialFailureReason.AuthOwnerRefreshRequired,
            exception.Reason
        );
        AssertRedacted(exception, fixture, token, "account-expired");
    }

    [Fact]
    public async Task GetCredentialAsync_RejectsMalformedAndDuplicateDocuments() {
        using var fixture = new AuthFixture();
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now)
        );

        foreach (string json in new[] {
            "{\"auth_mode\":\"chatgpt\",",
            """
            {"auth_mode":"chatgpt","AUTH_MODE":"chatgpt","tokens":{}}
            """,
            """
            {"auth_mode":"chatgpt","tokens":{"access_token":"one","access_token":"two","account_id":"account-1"}}
            """,
            """
            {"auth_mode":"chatgpt","tokens":{"access_token":"not-a-jwt","account_id":"account-1"}}
            """
        }) {
            fixture.WriteRaw(json);
            CodexSubscriptionCredentialException exception =
                await Assert.ThrowsAsync<CodexSubscriptionCredentialException>(
                    () => provider.GetCredentialAsync().AsTask()
                );
            Assert.Equal(
                CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed,
                exception.Reason
            );
            AssertRedacted(exception, fixture, json, "account-1");
        }
    }

    [Fact]
    public async Task GetCredentialAsync_ClassifiesAuthModeAndAccountFailures() {
        using var fixture = new AuthFixture();
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now)
        );
        string token = CreateAccessToken(Now.AddHours(1), "claim-account");

        fixture.WriteAuth(token, "claim-account", authMode: "apikey");
        await AssertReason(
            provider,
            CodexSubscriptionCredentialFailureReason.UnsupportedAuthMode
        );

        fixture.WriteAuth(token, accountId: null);
        await AssertReason(
            provider,
            CodexSubscriptionCredentialFailureReason.AuthAccountMissing
        );

        fixture.WriteAuth(token, "stored-account");
        await AssertReason(
            provider,
            CodexSubscriptionCredentialFailureReason.AuthAccountMismatch
        );
    }

    [Fact]
    public async Task GetCredentialAsync_AcceptsReadableFileModes() {
        using var fixture = new AuthFixture();
        string token = CreateAccessToken(Now.AddHours(1), "account-mode");
        fixture.WriteAuth(token, "account-mode");

        foreach (UnixFileMode mode in new[] {
            UnixFileMode.UserRead,
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            UnixFileMode.UserRead | UnixFileMode.GroupRead,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute
        }) {
            File.SetUnixFileMode(fixture.AuthFilePath, mode);
            var provider = new CodexCliAuthFileCredentialProvider(
                fixture.AuthFilePath,
                new FixedTimeProvider(Now)
            );
            CodexSubscriptionCredential credential =
                await provider.GetCredentialAsync();
            Assert.Equal("account-mode", credential.AccountId);
        }
    }

    [Fact]
    public async Task GetCredentialAsync_EnforcesFileAndSecretBounds() {
        using var fixture = new AuthFixture();
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now)
        );

        fixture.WriteRaw(new string(
            'x',
            CodexCliAuthFileCredentialProvider.MaximumAuthFileBytes + 1
        ));
        await AssertReason(
            provider,
            CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed
        );

        fixture.WriteAuth(
            new string(
                't',
                CodexCliAuthFileCredentialProvider.MaximumSecretUtf8Bytes + 1
            ),
            "account-bound"
        );
        await AssertReason(
            provider,
            CodexSubscriptionCredentialFailureReason.AuthSnapshotMalformed
        );
    }

    [Fact]
    public async Task GetCredentialAsync_AcceptsReadableSharedDirectory() {
        using var fixture = new AuthFixture();
        string token = CreateAccessToken(Now.AddHours(1), "account-directory");
        fixture.WriteAuth(token, "account-directory");
        File.SetUnixFileMode(
            fixture.RootPath,
            UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute
        );
        try {
            var provider = new CodexCliAuthFileCredentialProvider(
                fixture.AuthFilePath,
                new FixedTimeProvider(Now)
            );
            CodexSubscriptionCredential credential =
                await provider.GetCredentialAsync();
            Assert.Equal("account-directory", credential.AccountId);
        }
        finally {
            File.SetUnixFileMode(
                fixture.RootPath,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
            );
        }
    }

    [Fact]
    public async Task GetCredentialAsync_ClassifiesMissingFileWithoutPathLeak() {
        using var fixture = new AuthFixture();
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now)
        );

        CodexSubscriptionCredentialException exception =
            await Assert.ThrowsAsync<CodexSubscriptionCredentialException>(
                () => provider.GetCredentialAsync().AsTask()
            );

        Assert.Equal(
            CodexSubscriptionCredentialFailureReason.AuthStorageUnavailable,
            exception.Reason
        );
        AssertRedacted(exception, fixture);
    }

    [Fact]
    public async Task GetCredentialAsync_RejectsSymlinkedAndNonRegularPaths() {
        using var fixture = new AuthFixture();
        string token = CreateAccessToken(Now.AddHours(1), "account-link");
        fixture.WriteAuth(token, "account-link");

        string directoryAsFile = Path.Combine(
            fixture.RootPath,
            "directory-as-auth"
        );
        Directory.CreateDirectory(directoryAsFile);
        var directoryAsFileProvider = new CodexCliAuthFileCredentialProvider(
            directoryAsFile,
            new FixedTimeProvider(Now)
        );
        await AssertReason(
            directoryAsFileProvider,
            CodexSubscriptionCredentialFailureReason.CredentialStorageUnsafe
        );

        string fileLink = Path.Combine(fixture.RootPath, "auth-link.json");
        File.CreateSymbolicLink(fileLink, fixture.AuthFilePath);
        var fileLinkProvider = new CodexCliAuthFileCredentialProvider(
            fileLink,
            new FixedTimeProvider(Now)
        );
        await AssertReason(
            fileLinkProvider,
            CodexSubscriptionCredentialFailureReason.CredentialStorageUnsafe
        );

        string directoryLink = Path.Combine(
            Path.GetDirectoryName(fixture.RootPath)!,
            $"atelia-codex-auth-link-{Guid.NewGuid():N}"
        );
        Directory.CreateSymbolicLink(directoryLink, fixture.RootPath);
        try {
            var directoryLinkProvider = new CodexCliAuthFileCredentialProvider(
                Path.Combine(directoryLink, "auth.json"),
                new FixedTimeProvider(Now)
            );
            await AssertReason(
                directoryLinkProvider,
                CodexSubscriptionCredentialFailureReason
                    .CredentialStorageUnsafe
            );
        }
        finally {
            Directory.Delete(directoryLink);
        }

        string realAncestor = Path.Combine(fixture.RootPath, "real-ancestor");
        string nestedDirectory = Path.Combine(realAncestor, "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.SetUnixFileMode(
            nestedDirectory,
            UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
        );
        string nestedAuthFile = Path.Combine(nestedDirectory, "auth.json");
        File.Copy(fixture.AuthFilePath, nestedAuthFile);
        File.SetUnixFileMode(
            nestedAuthFile,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
        );
        string ancestorLink = Path.Combine(
            fixture.RootPath,
            "ancestor-link"
        );
        Directory.CreateSymbolicLink(ancestorLink, realAncestor);
        try {
            var ancestorLinkProvider = new CodexCliAuthFileCredentialProvider(
                Path.Combine(ancestorLink, "nested", "auth.json"),
                new FixedTimeProvider(Now)
            );
            await AssertReason(
                ancestorLinkProvider,
                CodexSubscriptionCredentialFailureReason
                    .CredentialStorageUnsafe
            );
        }
        finally {
            Directory.Delete(ancestorLink);
        }
    }

    [Fact]
    public async Task DefaultProvider_UsesAbsoluteCodexHomeAndRejectsRelativeValue() {
        using var fixture = new AuthFixture();
        string token = CreateAccessToken(
            DateTimeOffset.UtcNow.AddHours(1),
            "account-default"
        );
        fixture.WriteAuth(token, "account-default");
        string? original = Environment.GetEnvironmentVariable("CODEX_HOME");
        try {
            Environment.SetEnvironmentVariable("CODEX_HOME", fixture.RootPath);
            var provider = new CodexCliAuthFileCredentialProvider();
            CodexSubscriptionCredential credential =
                await provider.GetCredentialAsync();
            Assert.Equal("account-default", credential.AccountId);

            Environment.SetEnvironmentVariable("CODEX_HOME", "relative-codex-home");
            CodexSubscriptionCredentialException exception = Assert.Throws<
                CodexSubscriptionCredentialException
            >(() => new CodexCliAuthFileCredentialProvider());
            Assert.Equal(
                CodexSubscriptionCredentialFailureReason.CredentialPathInvalid,
                exception.Reason
            );
            Assert.DoesNotContain(
                "relative-codex-home",
                exception.ToString(),
                StringComparison.Ordinal
            );
        }
        finally {
            Environment.SetEnvironmentVariable("CODEX_HOME", original);
        }
    }

    [Fact]
    public async Task GetCredentialAsync_AcceptsDifferentFileOwnerWhenReadable() {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0) {
            return;
        }

        using var fixture = new AuthFixture();
        string token = CreateAccessToken(Now.AddHours(1), "account-owner");
        fixture.WriteAuth(token, "account-owner");
        Assert.Equal(0, ChangeOwner(fixture.AuthFilePath, 65534, uint.MaxValue));
        try {
            var provider = new CodexCliAuthFileCredentialProvider(
                fixture.AuthFilePath,
                new FixedTimeProvider(Now)
            );
            CodexSubscriptionCredential credential =
                await provider.GetCredentialAsync();
            Assert.Equal("account-owner", credential.AccountId);
        }
        finally {
            Assert.Equal(
                0,
                ChangeOwner(
                    fixture.AuthFilePath,
                    GetEffectiveUserId(),
                    uint.MaxValue
                )
            );
        }
    }

    [Fact]
    public async Task FailuresDoNotExposeCredentialOrPathCanaries() {
        using var fixture = new AuthFixture();
        string token = CreateAccessToken(Now.AddHours(1), "account-secret");
        fixture.WriteRaw($"{{\"secret\":\"{token}\"");
        var provider = new CodexCliAuthFileCredentialProvider(
            fixture.AuthFilePath,
            new FixedTimeProvider(Now)
        );

        CodexSubscriptionCredentialException exception =
            await Assert.ThrowsAsync<CodexSubscriptionCredentialException>(
                () => provider.GetCredentialAsync().AsTask()
            );

        AssertRedacted(exception, fixture, token, "account-secret");
        Assert.Null(exception.InnerException);
    }

    private static async Task AssertReason(
        CodexCliAuthFileCredentialProvider provider,
        CodexSubscriptionCredentialFailureReason expected
    ) {
        CodexSubscriptionCredentialException exception =
            await Assert.ThrowsAsync<CodexSubscriptionCredentialException>(
                () => provider.GetCredentialAsync().AsTask()
            );
        Assert.Equal(expected, exception.Reason);
    }

    private static void AssertRedacted(
        Exception exception,
        AuthFixture fixture,
        params string[] canaries
    ) {
        string text = exception.ToString();
        Assert.DoesNotContain(fixture.AuthFilePath, text, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.RootPath, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthFixture.RefreshCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthFixture.IdTokenCanary, text, StringComparison.Ordinal);
        foreach (string canary in canaries) {
            Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
        }
    }

    private static string CreateAccessToken(
        DateTimeOffset expiresAt,
        string? accountId,
        string nonce = "default"
    ) {
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(new {
            alg = "none",
            typ = "JWT"
        });
        var auth = new Dictionary<string, object?>(StringComparer.Ordinal) {
            ["chatgpt_account_id"] = accountId
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object?>(StringComparer.Ordinal) {
                ["exp"] = expiresAt.ToUnixTimeSeconds(),
                ["nonce"] = nonce,
                ["https://api.openai.com/auth"] = auth
            }
        );
        try {
            return $"{Base64Url.EncodeToString(header)}."
                + $"{Base64Url.EncodeToString(payload)}.signature";
        }
        finally {
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static string ComputeExpectedFingerprint(string accountId) {
        byte[] bytes = Encoding.UTF8.GetBytes(
            "atelia-chatgpt-account-v1\0" + accountId
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AuthFixture : IDisposable {
        public const string IdTokenCanary = "id-token-must-not-materialize";
        public const string RefreshCanary = "refresh-token-must-not-materialize";

        public AuthFixture() {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                $"atelia-codex-auth-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(RootPath);
            File.SetUnixFileMode(
                RootPath,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
            );
            AuthFilePath = Path.Combine(RootPath, "auth.json");
        }

        public string RootPath { get; }

        public string AuthFilePath { get; }

        public void WriteAuth(
            string accessToken,
            string? accountId,
            string authMode = "chatgpt",
            string? lastRefresh = null
        ) {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new {
                auth_mode = authMode,
                tokens = new {
                    id_token = IdTokenCanary,
                    access_token = accessToken,
                    refresh_token = RefreshCanary,
                    account_id = accountId
                },
                last_refresh = lastRefresh
            });
            try {
                File.WriteAllBytes(AuthFilePath, bytes);
                File.SetUnixFileMode(
                    AuthFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                );
            }
            finally {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public void WriteRaw(string json) {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            try {
                File.WriteAllBytes(AuthFilePath, bytes);
                File.SetUnixFileMode(
                    AuthFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                );
            }
            finally {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public void Dispose() {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "chown")]
    private static extern int ChangeOwner(
        string path,
        uint owner,
        uint group
    );
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CodexEnvironmentCollection {
    public const string Name = "Codex credential environment";
}
