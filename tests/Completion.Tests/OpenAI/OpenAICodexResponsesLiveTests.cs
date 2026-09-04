using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
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
    private const string EnableAgentControlEnv =
        "ATELIA_RUN_CODEX_SUBSCRIPTION_AGENT_CONTROL_LIVE";
    private const string RawLogPathEnv =
        "ATELIA_CODEX_SUBSCRIPTION_LIVE_RAW_LOG";

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

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_ExplicitCodexAuthFile_AcceptsAgentControlToolShape() {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableAgentControlEnv),
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
            ?? "gpt-5.6-sol";
        string originator = Environment.GetEnvironmentVariable(
            OriginatorEnv
        ) ?? "atelia-live-tool-probe";

        var provider = new CodexCliAuthFileCredentialProvider(authFile);
        CodexSubscriptionCredential snapshot =
            await provider.GetCredentialAsync(CancellationToken.None);
        var options = new OpenAICodexResponsesClientOptions {
            ExpectedAccountFingerprint = snapshot.AccountFingerprint,
            Originator = originator,
            ProductName = "Atelia",
            ProductVersion = "live-agent-control",
            MaxConcurrentRequests = 1,
            ReasoningEffort = CompletionReasoningEffort.Max
        };
        string? rawLogPath = Environment.GetEnvironmentVariable(
            RawLogPathEnv
        );
        using var client = CreateLiveClient(
            provider,
            options,
            rawLogPath
        );
        ToolDefinition tool = CreateAgentControlTool();
        var request = new CompletionRequest(
            model,
            new CompletionPromptPrefix(
                "Do not call any tool. Reply with exactly OK.",
                CompletionOutputContract.ProviderDefault([tool]),
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
        if (rawLogPath is not null) {
            Assert.True(File.Exists(rawLogPath));
            if (!OperatingSystem.IsWindows()) {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(rawLogPath)
                );
            }
        }
    }

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_SingletonRequiredNamedReturnsNamedToolCall() {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableAgentControlEnv),
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
            ?? "gpt-5.6-sol";
        string originator = Environment.GetEnvironmentVariable(
            OriginatorEnv
        ) ?? "atelia-live-tool-probe";

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
                ProductVersion = "live-required-named",
                MaxConcurrentRequests = 1,
                ReasoningEffort = CompletionReasoningEffort.Max
            }
        );
        ToolDefinition tool = CreateAgentControlTool();
        var request = new CompletionRequest(
            model,
            new CompletionPromptPrefix(
                "Call recap_grid_control exactly once with action inspect. Do not answer with text.",
                new CompletionOutputContract(
                    [tool],
                    CompletionToolChoice.RequiredNamed(
                        "recap_grid_control"
                    ),
                    allowParallelToolCalls: false
                ),
                [new ObservationMessage("Inspect the current control state.")]
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
        ActionBlock.ToolCall toolCall = Assert.Single(
            result.Message.Blocks.OfType<ActionBlock.ToolCall>()
        );
        Assert.Equal("recap_grid_control", toolCall.Call.ToolName);
        Assert.False(string.IsNullOrWhiteSpace(toolCall.Call.ToolCallId));
        using JsonDocument arguments = JsonDocument.Parse(
            toolCall.Call.RawArgumentsJson
        );
        Assert.Equal(
            "inspect",
            arguments.RootElement.GetProperty("action").GetString()
        );
    }

    private static OpenAICodexResponsesClient CreateLiveClient(
        ICodexSubscriptionCredentialProvider provider,
        OpenAICodexResponsesClientOptions options,
        string? rawLogPath
    ) {
        if (rawLogPath is null) {
            return new OpenAICodexResponsesClient(provider, options);
        }
        if (!Path.IsPathFullyQualified(rawLogPath)) {
            throw new InvalidOperationException(
                $"{RawLogPathEnv} must be an absolute path."
            );
        }
        if (File.Exists(rawLogPath)) {
            throw new InvalidOperationException(
                $"{RawLogPathEnv} must name a fresh ephemeral file."
            );
        }
        HttpMessageHandler handler = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(
                OpenAICodexResponsesClient.CreateProductionHandler()
            )
            .AddJsonLinesGoldenLogSink(rawLogPath)
            .BuildHandler();
        return new OpenAICodexResponsesClient(provider, options, handler);
    }

    // Mirrors RecapGridAgentControlTool.CanonicalDefinition without taking a
    // production project reference from Completion.Tests. The RecapGrid suite
    // separately pins the reflected canonical schema.
    private static ToolDefinition CreateAgentControlTool() => new(
        "recap_grid_control",
        "Inspect or mutate the admitted RecapGrid Control state. No authority tokens are accepted from the model.",
        new ToolSchema.Object([
            new ToolSchema.Property(
                "action",
                new ToolSchema.Value(
                    ToolParamType.String,
                    stringEnumValues: [
                        "inspect",
                        "register-family",
                        "register-definition",
                        "register-recipe",
                        "provision-built-in",
                        "promote"
                    ]
                ),
                isRequired: true
            ),
            new ToolSchema.Property(
                "canonicalValueBase64",
                new ToolSchema.Value(
                    ToolParamType.String,
                    maxLength: 1024 * 1024
                ),
                isRequired: false
            ),
            new ToolSchema.Property(
                "builtInAssetId",
                new ToolSchema.Value(
                    ToolParamType.String,
                    maxLength: 128
                ),
                isRequired: false
            ),
            new ToolSchema.Property(
                "recipeDigest",
                new ToolSchema.Value(
                    ToolParamType.String,
                    minLength: 64,
                    maxLength: 64
                ),
                isRequired: false
            )
        ])
    );
}
