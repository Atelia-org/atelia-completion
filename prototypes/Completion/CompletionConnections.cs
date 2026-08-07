using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;

namespace Atelia.Completion;

public sealed record CompletionConnectionsFileConfig(
    IReadOnlyList<CompletionConnectionConfig> Connections,
    string? DefaultConnectionId = null
);

public sealed record CompletionConnectionConfig(
    string Id,
    string Kind,
    string ModelId,
    string CompletionSurfaceId,
    string BaseAddress,
    string? ApiKey = null,
    string? BaseAddressEnv = null,
    string? ApiKeyEnv = null,
    int? MaxTokens = null,
    /// <summary>
    /// Provider-neutral reasoning preset. <see cref="CompletionReasoningEffort.ProviderDefault"/>
    /// preserves the selected provider/model default.
    /// </summary>
    CompletionReasoningEffort ReasoningEffort = CompletionReasoningEffort.ProviderDefault,
    /// <summary>
    /// Anthropic-specific prompt-cache TTL. The provider default preserves the
    /// existing wire shape by omitting <c>cache_control.ttl</c>.
    /// </summary>
    AnthropicPromptCacheTtl AnthropicPromptCacheTtl =
        AnthropicPromptCacheTtl.ProviderDefault
);

internal static class CompletionConnectionConfigValidation {
    public static void ValidateAnthropicPromptCacheTtl(
        CompletionConnectionConfig connection
    ) {
        if (!Enum.IsDefined(connection.AnthropicPromptCacheTtl)) {
            throw new InvalidOperationException(
                $"Completion connection '{connection.Id}' has unsupported anthropicPromptCacheTtl value "
                + $"'{connection.AnthropicPromptCacheTtl}'."
            );
        }
        if (connection.AnthropicPromptCacheTtl
                is not AnthropicPromptCacheTtl.ProviderDefault
            && !string.Equals(
                connection.Kind,
                "anthropic",
                StringComparison.OrdinalIgnoreCase
            )) {
            throw new InvalidOperationException(
                $"Completion connection '{connection.Id}' sets anthropicPromptCacheTtl, "
                + $"but kind '{connection.Kind}' is not 'anthropic'."
            );
        }
    }
}

public static class CompletionConnectionConfigLoader {
    public static CompletionConnectionsFileConfig LoadFile(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string resolvedPath = Path.GetFullPath(path);
        if (!File.Exists(resolvedPath)) { throw new FileNotFoundException($"Completion connections file was not found: {resolvedPath}", resolvedPath); }

        var config = JsonSerializer.Deserialize(File.ReadAllText(resolvedPath), CompletionJsonContext.Default.CompletionConnectionsFileConfig)
            ?? throw new InvalidOperationException($"Failed to deserialize Completion connections file: {resolvedPath}");

        return NormalizeAndValidate(config);
    }

    private static CompletionConnectionsFileConfig NormalizeAndValidate(CompletionConnectionsFileConfig config) {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Connections is not { Count: > 0 }) { throw new InvalidOperationException("Completion connections file must contain at least one connection."); }

        var connectionIds = new HashSet<string>(StringComparer.Ordinal);
        var resolvedConnections = new List<CompletionConnectionConfig>(config.Connections.Count);

        for (int i = 0; i < config.Connections.Count; i++) {
            var connection = config.Connections[i] ?? throw new InvalidOperationException($"Completion connection[{i}] must not be null.");
            RequireNonBlank(connection.Id, $"Completion connection[{i}] must have a non-empty id.");
            if (!connectionIds.Add(connection.Id)) { throw new InvalidOperationException($"Completion connections contain duplicate id '{connection.Id}'."); }

            RequireNonBlank(connection.Kind, $"Completion connection '{connection.Id}' must have a non-empty kind.");
            RequireNonBlank(connection.ModelId, $"Completion connection '{connection.Id}' must have a non-empty modelId.");
            // completionSurfaceId only disambiguates the openai-chat dialect; for single-surface
            // kinds (anthropic, openai-responses) it is redundant, so default it from the kind when
            // omitted instead of forcing every connection to spell it out.
            string completionSurfaceId = ResolveCompletionSurfaceId(connection.Kind, connection.CompletionSurfaceId);

            string baseAddress = connection.BaseAddress;
            string? apiKey = connection.ApiKey;

            if (!string.IsNullOrWhiteSpace(connection.BaseAddressEnv)) {
                string? resolved = Environment.GetEnvironmentVariable(connection.BaseAddressEnv);
                if (string.IsNullOrWhiteSpace(resolved)) {
                    throw new InvalidOperationException(
                        $"Completion connection '{connection.Id}' baseAddressEnv references environment variable "
                        + $"'{connection.BaseAddressEnv}', but it is not set or empty."
                    );
                }
                baseAddress = resolved;
            }

            if (!string.IsNullOrWhiteSpace(connection.ApiKeyEnv)) {
                string? resolved = Environment.GetEnvironmentVariable(connection.ApiKeyEnv);
                if (string.IsNullOrWhiteSpace(resolved)) {
                    throw new InvalidOperationException(
                        $"Completion connection '{connection.Id}' apiKeyEnv references environment variable "
                        + $"'{connection.ApiKeyEnv}', but it is not set or empty."
                    );
                }
                apiKey = resolved;
            }

            RequireNonBlank(baseAddress, $"Completion connection '{connection.Id}' must have a non-empty baseAddress.");
            if (!Enum.IsDefined(connection.ReasoningEffort)) {
                throw new InvalidOperationException(
                    $"Completion connection '{connection.Id}' has unsupported reasoningEffort value '{connection.ReasoningEffort}'."
                );
            }
            CompletionConnectionConfigValidation
                .ValidateAnthropicPromptCacheTtl(connection);

            resolvedConnections.Add(connection with { CompletionSurfaceId = completionSurfaceId, BaseAddress = baseAddress, ApiKey = apiKey });
        }

        string defaultConnectionId = !string.IsNullOrWhiteSpace(config.DefaultConnectionId)
            ? config.DefaultConnectionId!
            : resolvedConnections[0].Id;

        if (!connectionIds.Contains(defaultConnectionId)) { throw new InvalidOperationException($"Completion defaultConnectionId '{defaultConnectionId}' does not match any connection id."); }

        return new CompletionConnectionsFileConfig(resolvedConnections, defaultConnectionId);
    }

    private static void RequireNonBlank(string? value, string message) {
        if (string.IsNullOrWhiteSpace(value)) { throw new InvalidOperationException(message); }
    }

    private static string ResolveCompletionSurfaceId(string kind, string? explicitSurfaceId) {
        if (!string.IsNullOrWhiteSpace(explicitSurfaceId)) { return explicitSurfaceId; }

        // openai-chat defaults to the strict dialect, matching DefaultCompletionClientFactory's
        // fallback; anthropic/openai-responses have a single surface. Unknown kinds fall back to
        // the kind itself so the value stays non-blank for logging/storage.
        return kind.Trim().ToLowerInvariant() switch {
            "openai-chat" => "openai-chat/strict",
            "openai-responses" => "openai-responses",
            "anthropic" => "anthropic",
            _ => kind.Trim()
        };
    }
}

public interface ICompletionClientFactory {
    ICompletionClient Create(CompletionConnectionConfig connection);
}

public sealed class DefaultCompletionClientFactory : ICompletionClientFactory {
    public ICompletionClient Create(CompletionConnectionConfig connection) {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateReasoningConfiguration(connection);
        CompletionConnectionConfigValidation
            .ValidateAnthropicPromptCacheTtl(connection);

        var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
            new Uri(connection.BaseAddress, UriKind.Absolute)
        );
        try {
            ICompletionClient client = connection.Kind.Trim().ToLowerInvariant() switch {
                "openai-chat" => new OpenAIChatClient(
                    apiKey: connection.ApiKey,
                    httpClient: httpClient,
                    dialect: ResolveOpenAiChatDialect(connection.CompletionSurfaceId),
                    options: new OpenAIChatClientOptions {
                        ReasoningEffort = connection.ReasoningEffort
                    }
                ),
                "openai-responses" => new OpenAIResponsesClient(
                    apiKey: connection.ApiKey,
                    httpClient: httpClient,
                    options: new OpenAIResponsesClientOptions {
                        ReasoningEffort = connection.ReasoningEffort
                    }
                ),
                "anthropic" => new AnthropicClient(
                    apiKey: connection.ApiKey,
                    httpClient: httpClient,
                    defaultMaxTokens: connection.MaxTokens,
                    reasoningEffort: connection.ReasoningEffort,
                    promptCacheTtl: connection.AnthropicPromptCacheTtl
                ),
                _ => throw new InvalidOperationException($"Unsupported completion connection kind '{connection.Kind}'.")
            };

            return new OwnedHttpCompletionClient(
                client,
                httpClient
            );
        }
        catch {
            httpClient.Dispose();
            throw;
        }
    }

    private static OpenAIChatDialect ResolveOpenAiChatDialect(string completionSurfaceId) {
        return completionSurfaceId switch {
            "openai-chat/strict" => OpenAIChatDialects.Strict,
            "openai-chat/sglang-compatible" => OpenAIChatDialects.SgLangCompatible,
            "openai-chat/qwen-sglang" => OpenAIChatDialects.QwenSgLang,
            "openai-chat/deepseek-v4" => OpenAIChatDialects.DeepSeekV4,
            _ => throw new InvalidOperationException(
                $"Unsupported openai-chat completion surface '{completionSurfaceId}'."
            )
        };
    }

    private static void ValidateReasoningConfiguration(
        CompletionConnectionConfig connection
    ) {
        if (!Enum.IsDefined(connection.ReasoningEffort)) {
            throw new InvalidOperationException(
                $"Completion connection '{connection.Id}' has unsupported reasoningEffort value '{connection.ReasoningEffort}'."
            );
        }
        if (connection.ReasoningEffort is CompletionReasoningEffort.ProviderDefault) { return; }

        if (string.Equals(connection.Kind, "openai-chat", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                connection.CompletionSurfaceId,
                "openai-chat/sglang-compatible",
                StringComparison.Ordinal
            )) {
            throw new InvalidOperationException(
                "The generic openai-chat/sglang-compatible surface has no provider-neutral reasoning mapping. "
                + "Use openai-chat/qwen-sglang for Qwen's enable_thinking control."
            );
        }
    }
}

internal sealed class OwnedHttpCompletionClient : ICompletionClient, IDisposable {
    private readonly ICompletionClient _inner;
    private readonly HttpClient _httpClient;

    public OwnedHttpCompletionClient(
        ICompletionClient inner,
        HttpClient httpClient
    ) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string Name => _inner.Name;

    public string ApiSpecId => _inner.ApiSpecId;

    internal TimeSpan HttpClientTimeout => _httpClient.Timeout;

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => _inner.StreamCompletionAsync(
        request,
        observer,
        cancellationToken
    );

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(invocationOptions);
        invocationOptions.Validate();
        return _inner.StreamCompletionAsync(
            request,
            invocationOptions,
            observer,
            cancellationToken
        );
    }

    public void Dispose() {
        if (_inner is IDisposable disposable) { disposable.Dispose(); }
        _httpClient.Dispose();
    }
}

public sealed class CompletionConnectionRegistry : IDisposable {
    private readonly ICompletionClientFactory _factory;
    private readonly IReadOnlyDictionary<string, CompletionConnectionConfig> _byId;
    private readonly ConcurrentDictionary<string, ICompletionClient> _clients = new(StringComparer.Ordinal);

    public CompletionConnectionRegistry(CompletionConnectionsFileConfig config, ICompletionClientFactory factory) {
        ArgumentNullException.ThrowIfNull(config);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Connections = config.Connections;
        DefaultConnectionId = config.DefaultConnectionId ?? throw new ArgumentException("Default connection id must not be null.", nameof(config));
        _byId = config.Connections.ToDictionary(static x => x.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<CompletionConnectionConfig> Connections { get; }

    public string DefaultConnectionId { get; }

    public bool TryGet(string id, out CompletionConnectionConfig connection)
        => _byId.TryGetValue(id, out connection!);

    public CompletionConnectionConfig Resolve(string? requestedId) {
        if (!string.IsNullOrWhiteSpace(requestedId) && _byId.TryGetValue(requestedId, out var requested)) { return requested; }

        return _byId.TryGetValue(DefaultConnectionId, out var fallback)
            ? fallback
            : throw new InvalidOperationException($"Default connection '{DefaultConnectionId}' is not registered.");
    }

    public ICompletionClient GetClient(string connectionId) {
        if (!_byId.TryGetValue(connectionId, out var connection)) { throw new InvalidOperationException($"Unknown completion connection '{connectionId}'."); }

        return _clients.GetOrAdd(
            connection.Id,
            static (_, state) => state.Factory.Create(state.Connection),
            (Factory: _factory, Connection: connection)
        );
    }

    /// <summary>
    /// Binds an exact durable dispatch identity without falling back to the
    /// default connection. Connection metadata is validated before a concrete
    /// client is created; adapter identity is validated after creation.
    /// </summary>
    public CompletionDispatchBindingResult BindExact(
        CompletionDispatchIdentity required
    ) {
        ArgumentNullException.ThrowIfNull(required);
        if (!_byId.TryGetValue(
            required.ConnectionId,
            out CompletionConnectionConfig? connection
        )) {
            return Unavailable(
                CompletionDispatchBindingUnavailableReason
                    .ConnectionMissing,
                $"Required completion connection "
                + $"'{required.ConnectionId}' is not registered."
            );
        }
        if (!string.Equals(
            connection.Kind,
            required.Kind,
            StringComparison.Ordinal
        )) {
            return Unavailable(
                CompletionDispatchBindingUnavailableReason
                    .ConnectionKindMismatch,
                $"Completion connection '{required.ConnectionId}' kind "
                + "does not match the required dispatch identity."
            );
        }
        string connectionFingerprint =
            CompletionDispatchIdentityFactory
                .ComputeConnectionFingerprint(connection);
        if (!string.Equals(
            connectionFingerprint,
            required.ConnectionFingerprint,
            StringComparison.Ordinal
        )) {
            return Unavailable(
                CompletionDispatchBindingUnavailableReason
                    .ConnectionFingerprintMismatch,
                $"Completion connection '{required.ConnectionId}' "
                + "metadata does not match the required dispatch identity."
            );
        }

        ICompletionClient client = GetClient(connection.Id);
        if (!string.Equals(
            client.Name,
            required.ClientName,
            StringComparison.Ordinal
        )) {
            return Unavailable(
                CompletionDispatchBindingUnavailableReason
                    .ClientNameMismatch,
                $"Completion connection '{required.ConnectionId}' client "
                + "name does not match the required dispatch identity."
            );
        }
        if (!string.Equals(
            client.ApiSpecId,
            required.ApiSpecId,
            StringComparison.Ordinal
        )) {
            return Unavailable(
                CompletionDispatchBindingUnavailableReason
                    .ClientApiSpecIdMismatch,
                $"Completion connection '{required.ConnectionId}' client "
                + "API specification does not match the required dispatch "
                + "identity."
            );
        }
        string adapterFingerprint =
            CompletionDispatchIdentityFactory
                .ComputeRequestAdapterFingerprint(client, connection);
        if (!string.Equals(
            adapterFingerprint,
            required.RequestAdapterFingerprint,
            StringComparison.Ordinal
        )) {
            return Unavailable(
                CompletionDispatchBindingUnavailableReason
                    .RequestAdapterFingerprintMismatch,
                $"Completion connection '{required.ConnectionId}' request "
                + "adapter does not match the required dispatch identity."
            );
        }

        return new CompletionDispatchBindingResult.Bound(
            connection,
            client
        );
    }

    private static CompletionDispatchBindingResult.Unavailable Unavailable(
        CompletionDispatchBindingUnavailableReason reason,
        string detail
    ) => new(reason, detail);

    public void Dispose() {
        foreach (var client in _clients.Values) {
            if (client is IDisposable disposable) { disposable.Dispose(); }
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CompletionConnectionsFileConfig))]
[JsonSerializable(typeof(CompletionConnectionConfig))]
internal sealed partial class CompletionJsonContext : JsonSerializerContext;
