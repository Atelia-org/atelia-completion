using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class CodexSubscriptionCompletionClientFactoryTests {
    private const string ExpectedAccountFingerprint =
        "sha256:00000000000000000000000000000000"
        + "00000000000000000000000000000000";

    [Fact]
    public void CreateInterceptsExactCodexKindWithoutCredentialOrFallbackSideEffects() {
        var provider = new TrackingCredentialProvider();
        var fallback = new TrackingFallbackFactory();
        var factory = Factory(provider, fallback);

        using var client = Assert.IsType<OpenAICodexResponsesClient>(
            factory.Create(CodexConnection())
        );

        Assert.Equal("chatgpt.com", client.Name);
        Assert.Equal("openai-codex-responses-v2", client.ApiSpecId);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public void CreateAcceptsResolvedCanonicalBaseAddressFromEnvironmentSource() {
        var provider = new TrackingCredentialProvider();
        var fallback = new TrackingFallbackFactory();
        var factory = Factory(provider, fallback);
        CompletionConnectionConfig connection = CodexConnection() with {
            BaseAddressEnv = "ATELIA_TEST_CODEX_BASE"
        };

        using var client = Assert.IsType<OpenAICodexResponsesClient>(
            factory.Create(connection)
        );

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public void CreateDelegatesEveryOtherKindUnchanged() {
        var provider = new TrackingCredentialProvider();
        var fallback = new TrackingFallbackFactory();
        var factory = Factory(provider, fallback);
        CompletionConnectionConfig connection = CodexConnection() with {
            Kind = "custom-provider",
            CompletionSurfaceId = "custom-surface",
            BaseAddress = "https://custom.example/",
            ApiKey = "custom-key"
        };

        ICompletionClient client = factory.Create(connection);

        Assert.Same(fallback.Client, client);
        Assert.Same(connection, fallback.Connection);
        Assert.Equal(1, fallback.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData("Openai-codex-responses")]
    [InlineData("OPENAI-CODEX-RESPONSES")]
    [InlineData(" openai-codex-responses")]
    [InlineData("openai-codex-responses ")]
    public void CreateRejectsCodexKindPseudoVariantsBeforeSideEffects(
        string kind
    ) {
        var provider = new TrackingCredentialProvider();
        var fallback = new TrackingFallbackFactory();
        var factory = Factory(provider, fallback);

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException
        >(() => factory.Create(CodexConnection() with { Kind = kind }));

        Assert.Contains("exact kind", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    public static TheoryData<CompletionConnectionConfig, string>
        InvalidCodexConnections => new() {
            {
                CodexConnection() with {
                    CompletionSurfaceId = "openai-responses"
                },
                "completionSurfaceId"
            },
            {
                CodexConnection() with {
                    BaseAddress = "https://chatgpt.com/"
                },
                "baseAddress"
            },
            {
                CodexConnection() with {
                    BaseAddress =
                        "https://chatgpt.com/backend-api/codex"
                },
                "baseAddress"
            },
            {
                CodexConnection() with { ApiKey = "forbidden" },
                "apiKey"
            },
            {
                CodexConnection() with {
                    ApiKeyEnv = "FORBIDDEN_API_KEY"
                },
                "apiKeyEnv"
            },
            {
                CodexConnection() with {
                    AnthropicPromptCacheTtl =
                        AnthropicPromptCacheTtl.OneHour
                },
                "anthropicPromptCacheTtl"
            },
            {
                CodexConnection() with {
                    ReasoningEffort = (CompletionReasoningEffort)int.MaxValue
                },
                "reasoningEffort"
            }
        };

    [Theory]
    [MemberData(nameof(InvalidCodexConnections))]
    public void CreateRejectsInvalidCodexMetadataBeforeSideEffects(
        CompletionConnectionConfig connection,
        string expectedDetail
    ) {
        var provider = new TrackingCredentialProvider();
        var fallback = new TrackingFallbackFactory();
        var factory = Factory(provider, fallback);

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException
        >(() => factory.Create(connection));

        Assert.Contains(
            expectedDetail,
            exception.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void ConstructorRejectsInvalidConcurrency(int concurrency) {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CodexSubscriptionCompletionClientFactory(
                new TrackingCredentialProvider(),
                ExpectedAccountFingerprint,
                maxConcurrentRequests: concurrency
            )
        );
    }

    [Theory]
    [InlineData("SHA256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256:ABCDEF0000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256:0000")]
    public void ConstructorRejectsNonCanonicalAccountFingerprint(
        string fingerprint
    ) {
        Assert.Throws<ArgumentException>(() =>
            new CodexSubscriptionCompletionClientFactory(
                new TrackingCredentialProvider(),
                fingerprint
            )
        );
    }

    [Theory]
    [InlineData("Galatea")]
    [InlineData("not allowed")]
    [InlineData("")]
    public void ConstructorRejectsInvalidOriginator(string originator) {
        Assert.Throws<ArgumentException>(() =>
            new CodexSubscriptionCompletionClientFactory(
                new TrackingCredentialProvider(),
                ExpectedAccountFingerprint,
                originator
            )
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("Atelia Product")]
    [InlineData("Atelia/1")]
    public void ConstructorRejectsInvalidProductIdentity(
        string productName
    ) {
        Assert.Throws<ArgumentException>(() =>
            new CodexSubscriptionCompletionClientFactory(
                new TrackingCredentialProvider(),
                ExpectedAccountFingerprint,
                productName: productName
            )
        );
    }

    private static CodexSubscriptionCompletionClientFactory Factory(
        TrackingCredentialProvider provider,
        TrackingFallbackFactory fallback
    ) => new(
        provider,
        ExpectedAccountFingerprint,
        fallback: fallback,
        productName: "Atelia.Tests",
        productVersion: "1.0.0"
    );

    private static CompletionConnectionConfig CodexConnection() => new(
        Id: "codex",
        Kind: CodexSubscriptionCompletionClientFactory.ConnectionKind,
        ModelId: "gpt-test-codex",
        CompletionSurfaceId:
            CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
        BaseAddress:
            CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress,
        ReasoningEffort: CompletionReasoningEffort.ProviderDefault
    );

    private sealed class TrackingCredentialProvider
        : ICodexSubscriptionCredentialProvider {
        public int CallCount { get; private set; }

        public ValueTask<CodexSubscriptionCredential> GetCredentialAsync(
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            throw new InvalidOperationException(
                "Factory tests must not read credentials."
            );
        }
    }

    private sealed class TrackingFallbackFactory : ICompletionClientFactory {
        public ICompletionClient Client { get; } = new FallbackClient();
        public CompletionConnectionConfig? Connection { get; private set; }
        public int CallCount { get; private set; }

        public ICompletionClient Create(CompletionConnectionConfig connection) {
            Connection = connection;
            CallCount++;
            return Client;
        }
    }

    private sealed class FallbackClient : ICompletionClient {
        public string Name => "fallback";
        public string ApiSpecId => "fallback-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException(
            "Factory tests do not dispatch completions."
        );
    }
}
