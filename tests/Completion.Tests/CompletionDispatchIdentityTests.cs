using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class CompletionDispatchIdentityTests {
    [Fact]
    public void CreatePreservesConnectionIdentityAndExcludesSecrets() {
        CompletionConnectionConfig connection = CreateConnection();
        var client = new IdentityCompletionClient(
            "client-a",
            "api-a"
        );

        CompletionDispatchIdentity identity =
            CompletionDispatchIdentityFactory.Create(
                connection,
                client
            );
        CompletionDispatchIdentity changedSecrets =
            CompletionDispatchIdentityFactory.Create(
                connection with {
                    ApiKey = "different-secret",
                    ApiKeyEnv = "DIFFERENT_API_KEY_ENV",
                    BaseAddressEnv = "DIFFERENT_BASE_ADDRESS_ENV"
                },
                client
            );

        Assert.Equal("local-id", identity.ConnectionId);
        Assert.Equal("kind-a", identity.Kind);
        Assert.Equal("client-a", identity.ClientName);
        Assert.Equal("api-a", identity.ApiSpecId);
        Assert.Equal(
            "sha256:"
            + "d252b9c28316fb0440afe6bf9773be15"
            + "da4e5c52666959b02dceb9eaaf88275e",
            identity.ConnectionFingerprint
        );
        Assert.Equal(identity, changedSecrets);
    }

    [Fact]
    public void ConnectionFingerprintCoversEverySemanticFieldFamily() {
        CompletionConnectionConfig connection = CreateConnection();
        string connectionBaseline =
            CompletionDispatchIdentityFactory
                .ComputeConnectionFingerprint(connection);
        string[] connectionVariants = [
            CompletionDispatchIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with { Id = "local-b" }
                ),
            CompletionDispatchIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with { Kind = "kind-b" }
                ),
            CompletionDispatchIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with { ModelId = "model-b" }
                ),
            CompletionDispatchIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with {
                        CompletionSurfaceId = "surface-b"
                    }
                ),
            CompletionDispatchIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with {
                        BaseAddress = "https://b.example/v1/"
                    }
                ),
            CompletionDispatchIdentityFactory
                .ComputeConnectionFingerprint(
                    connection with {
                        ReasoningEffort = CompletionReasoningEffort.High
                    }
                )
        ];
        Assert.All(
            connectionVariants,
            fingerprint => Assert.NotEqual(
                connectionBaseline,
                fingerprint
            )
        );
        Assert.Equal(
            connectionVariants.Length,
            connectionVariants.Distinct(StringComparer.Ordinal).Count()
        );
    }

    [Fact]
    public void ConnectionFingerprintExcludesAnthropicPromptCacheTtlOperationalPolicy() {
        CompletionConnectionConfig connection = CreateConnection() with {
            Kind = "anthropic",
            CompletionSurfaceId = "anthropic"
        };
        CompletionConnectionConfig changedTtl = connection with {
            AnthropicPromptCacheTtl = AnthropicPromptCacheTtl.OneHour
        };

        Assert.Equal(
            CompletionDispatchIdentityFactory.ComputeConnectionFingerprint(
                connection
            ),
            CompletionDispatchIdentityFactory.ComputeConnectionFingerprint(
                changedTtl
            )
        );
    }

    [Fact]
    public void BindExactMissingConnectionDoesNotFallbackOrCreateClient() {
        CompletionConnectionConfig connection = CreateConnection();
        var factory = new RecordingClientFactory(
            new IdentityCompletionClient("client-a", "api-a")
        );
        using var registry = CreateRegistry(connection, factory);
        CompletionDispatchIdentity required =
            CompletionDispatchIdentityFactory.Create(
                connection with { Id = "missing-id" },
                factory.Client
            );

        var unavailable = Assert.IsType<
            CompletionDispatchBindingResult.Unavailable
        >(registry.BindExact(required));

        Assert.Equal(
            CompletionDispatchBindingUnavailableReason.ConnectionMissing,
            unavailable.Reason
        );
        Assert.Equal(0, factory.CallCount);
    }

    [Theory]
    [InlineData("kind", CompletionDispatchBindingUnavailableReason.ConnectionKindMismatch)]
    [InlineData("metadata", CompletionDispatchBindingUnavailableReason.ConnectionFingerprintMismatch)]
    [InlineData("endpoint", CompletionDispatchBindingUnavailableReason.ConnectionFingerprintMismatch)]
    [InlineData("reasoning", CompletionDispatchBindingUnavailableReason.ConnectionFingerprintMismatch)]
    [InlineData("surface", CompletionDispatchBindingUnavailableReason.ConnectionFingerprintMismatch)]
    public void BindExactConnectionMismatchDoesNotCreateClient(
        string mismatch,
        CompletionDispatchBindingUnavailableReason expectedReason
    ) {
        CompletionConnectionConfig connection = CreateConnection();
        var factory = new RecordingClientFactory(
            new IdentityCompletionClient("client-a", "api-a")
        );
        using var registry = CreateRegistry(connection, factory);
        CompletionConnectionConfig changed = mismatch switch {
            "kind" => connection with { Kind = "kind-b" },
            "metadata" => connection with { ModelId = "model-b" },
            "endpoint" => connection with { BaseAddress = "https://b.example/v1/" },
            "reasoning" => connection with { ReasoningEffort = CompletionReasoningEffort.High },
            "surface" => connection with { CompletionSurfaceId = "surface-b" },
            _ => throw new InvalidOperationException()
        };
        CompletionDispatchIdentity required =
            CompletionDispatchIdentityFactory.Create(
                changed,
                factory.Client
            );

        var unavailable = Assert.IsType<
            CompletionDispatchBindingResult.Unavailable
        >(registry.BindExact(required));

        Assert.Equal(expectedReason, unavailable.Reason);
        Assert.Equal(0, factory.CallCount);
    }

    [Fact]
    public void BindExactReturnsMatchingConnectionAndClient() {
        CompletionConnectionConfig connection = CreateConnection();
        var factory = new RecordingClientFactory(
            new IdentityCompletionClient("client-a", "api-a")
        );
        using var registry = CreateRegistry(connection, factory);
        CompletionDispatchIdentity required =
            CompletionDispatchIdentityFactory.Create(
                connection,
                factory.Client
            );

        var bound = Assert.IsType<
            CompletionDispatchBindingResult.Bound
        >(registry.BindExact(required));

        Assert.Same(connection, bound.Connection);
        Assert.Same(factory.Client, bound.Client);
        Assert.Equal(1, factory.CallCount);
    }

    [Theory]
    [InlineData("name", CompletionDispatchBindingUnavailableReason.ClientNameMismatch)]
    [InlineData("api", CompletionDispatchBindingUnavailableReason.ClientApiSpecIdMismatch)]
    public void BindExactReportsClientMismatch(
        string mismatch,
        CompletionDispatchBindingUnavailableReason expectedReason
    ) {
        CompletionConnectionConfig connection = CreateConnection();
        var actualClient = new IdentityCompletionClient(
            "client-a",
            "api-a"
        );
        var factory = new RecordingClientFactory(actualClient);
        using var registry = CreateRegistry(connection, factory);
        CompletionDispatchIdentity required =
            CompletionDispatchIdentityFactory.Create(
                connection,
                actualClient
            );
        required = mismatch switch {
            "name" => required with { ClientName = "client-b" },
            "api" => required with { ApiSpecId = "api-b" },
            _ => throw new InvalidOperationException()
        };

        var unavailable = Assert.IsType<
            CompletionDispatchBindingResult.Unavailable
        >(registry.BindExact(required));

        Assert.Equal(expectedReason, unavailable.Reason);
        Assert.Equal(1, factory.CallCount);
    }

    private static CompletionConnectionRegistry CreateRegistry(
        CompletionConnectionConfig connection,
        RecordingClientFactory factory
    ) => new(
        new CompletionConnectionsFileConfig(
            [connection],
            connection.Id
        ),
        factory
    );

    private static CompletionConnectionConfig CreateConnection()
        => new(
            Id: "local-id",
            Kind: "kind-a",
            ModelId: "model-a",
            CompletionSurfaceId: "surface-a",
            BaseAddress: "https://a.example/v1/",
            ApiKey: "secret-a",
            BaseAddressEnv: "BASE_ADDRESS_ENV",
            ApiKeyEnv: "API_KEY_ENV"
        );

    private sealed class RecordingClientFactory(
        ICompletionClient client
    ) : ICompletionClientFactory {
        public ICompletionClient Client { get; } = client;
        public int CallCount { get; private set; }

        public ICompletionClient Create(
            CompletionConnectionConfig connection
        ) {
            CallCount++;
            return Client;
        }
    }

    private sealed class IdentityCompletionClient(
        string name,
        string apiSpecId
    ) : ICompletionClient {
        public string Name { get; } = name;
        public string ApiSpecId { get; } = apiSpecId;

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException(
            "Identity tests do not invoke completion."
        );
    }
}
