using System.Runtime.ExceptionServices;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;

namespace Atelia.Completion;

public sealed record CompletionConnectionsFileConfig(
    IReadOnlyList<CompletionConnectionConfig> Connections,
    string? DefaultConnectionId = null,
    IReadOnlyList<string>? SelectableConnectionIds = null,
    IReadOnlyDictionary<string, string?>? Bindings = null
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
    /// <summary>
    /// Provider/client output setting. Business runtimes must not reinterpret
    /// it as an independent per-request budget.
    /// </summary>
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
    /// <summary>Maximum encoded size accepted by the V1 connections document.</summary>
    public const int MaximumInputUtf8Bytes = 1024 * 1024;

    /// <summary>
    /// Decodes the single strict V1 connections byte language. The root must
    /// contain exact integer <c>v: 1</c>, 1..256 connections, and an exact
    /// default connection id; nesting is capped at depth 8 and input at 1 MiB.
    /// </summary>
    public static CompletionConnectionsFileConfig Decode(
        ReadOnlySpan<byte> utf8Json
    ) => CompletionConnectionsManifestV1Reader.Decode(utf8Json);

    /// <summary>Reads a bounded ordinary file and delegates to <see cref="Decode"/>.</summary>
    public static CompletionConnectionsFileConfig LoadFile(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string resolvedPath = Path.GetFullPath(path);
        using var stream = new FileStream(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan
        );
        if (stream.Length is < 1 or > MaximumInputUtf8Bytes) {
            throw new InvalidDataException(
                "Completion connections bytes are empty or exceed the 1 MiB V1 bound."
            );
        }
        int length = checked((int)stream.Length);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        stream.ReadExactly(bytes);
        if (stream.Length != length || stream.ReadByte() != -1) {
            throw new InvalidDataException(
                "Completion connections file changed during its bounded read."
            );
        }
        return Decode(bytes);
    }

    /// <summary>
    /// Resolves environment-backed values and validates an already parsed
    /// shared connection-file payload.
    /// </summary>
    public static CompletionConnectionsFileConfig NormalizeAndValidate(
        CompletionConnectionsFileConfig config
    ) {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Connections is not { Count: > 0 }
            || config.Connections.Count
                > CompletionConnectionsManifestV1Reader
                    .MaximumConnectionCount) {
            throw new InvalidOperationException(
                "Completion connections must contain between 1 and 256 connections."
            );
        }

        var connectionIds = new HashSet<string>(StringComparer.Ordinal);
        var resolvedConnections = new List<CompletionConnectionConfig>(config.Connections.Count);

        for (int i = 0; i < config.Connections.Count; i++) {
            var connection = config.Connections[i] ?? throw new InvalidOperationException($"Completion connection[{i}] must not be null.");
            RequireNonBlank(connection.Id, $"Completion connection[{i}] must have a non-empty id.");
            RequireConfigBound(
                connection.Id,
                CompletionConnectionsManifestV1Reader
                    .MaximumIdentifierUtf8Bytes,
                "Completion connection id"
            );
            if (!connectionIds.Add(connection.Id)) { throw new InvalidOperationException($"Completion connections contain duplicate id '{connection.Id}'."); }

            RequireNonBlank(connection.Kind, $"Completion connection '{connection.Id}' must have a non-empty kind.");
            RequireNonBlank(connection.ModelId, $"Completion connection '{connection.Id}' must have a non-empty modelId.");
            RequireConfigBound(
                connection.Kind,
                CompletionConnectionsManifestV1Reader
                    .MaximumIdentifierUtf8Bytes,
                "Completion connection kind"
            );
            RequireConfigBound(
                connection.ModelId,
                CompletionConnectionsManifestV1Reader
                    .MaximumIdentifierUtf8Bytes,
                "Completion connection modelId"
            );
            // completionSurfaceId only disambiguates the openai-chat dialect; for single-surface
            // kinds (anthropic, openai-responses) it is redundant, so default it from the kind when
            // omitted instead of forcing every connection to spell it out.
            string completionSurfaceId = ResolveCompletionSurfaceId(connection.Kind, connection.CompletionSurfaceId);
            RequireConfigBound(
                completionSurfaceId,
                CompletionConnectionsManifestV1Reader
                    .MaximumIdentifierUtf8Bytes,
                "Completion connection completionSurfaceId"
            );

            string baseAddress = connection.BaseAddress;
            string? apiKey = connection.ApiKey;

            if (!string.IsNullOrWhiteSpace(connection.BaseAddress)) {
                RequireConfigBound(
                    connection.BaseAddress,
                    CompletionConnectionsManifestV1Reader
                        .MaximumEndpointUtf8Bytes,
                    "Completion connection baseAddress"
                );
            }
            if (connection.ApiKey is not null) {
                RequireConfigBound(
                    connection.ApiKey,
                    CompletionConnectionsManifestV1Reader
                        .MaximumSecretUtf8Bytes,
                    "Completion connection apiKey"
                );
            }

            if (!string.IsNullOrWhiteSpace(connection.BaseAddressEnv)) {
                RequireConfigBound(
                    connection.BaseAddressEnv,
                    CompletionConnectionsManifestV1Reader
                        .MaximumIdentifierUtf8Bytes,
                    "Completion connection baseAddressEnv"
                );
                string? resolved = ResolveEnvironmentVariable(
                    connection.BaseAddressEnv,
                    "baseAddressEnv"
                );
                if (string.IsNullOrWhiteSpace(resolved)) {
                    throw new InvalidOperationException(
                        $"Completion connection '{connection.Id}' baseAddressEnv references environment variable "
                        + $"'{connection.BaseAddressEnv}', but it is not set or empty."
                    );
                }
                baseAddress = resolved;
            }

            if (!string.IsNullOrWhiteSpace(connection.ApiKeyEnv)) {
                RequireConfigBound(
                    connection.ApiKeyEnv,
                    CompletionConnectionsManifestV1Reader
                        .MaximumIdentifierUtf8Bytes,
                    "Completion connection apiKeyEnv"
                );
                string? resolved = ResolveEnvironmentVariable(
                    connection.ApiKeyEnv,
                    "apiKeyEnv"
                );
                if (string.IsNullOrWhiteSpace(resolved)) {
                    throw new InvalidOperationException(
                        $"Completion connection '{connection.Id}' apiKeyEnv references environment variable "
                        + $"'{connection.ApiKeyEnv}', but it is not set or empty."
                    );
                }
                apiKey = resolved;
            }

            RequireNonBlank(baseAddress, $"Completion connection '{connection.Id}' must have a non-empty baseAddress.");
            RequireConfigBound(
                baseAddress,
                CompletionConnectionsManifestV1Reader
                    .MaximumEndpointUtf8Bytes,
                "Resolved Completion connection baseAddress"
            );
            if (apiKey is not null) {
                RequireConfigBound(
                    apiKey,
                    CompletionConnectionsManifestV1Reader
                        .MaximumSecretUtf8Bytes,
                    "Resolved Completion connection apiKey"
                );
            }
            if (!Enum.IsDefined(connection.ReasoningEffort)) {
                throw new InvalidOperationException(
                    $"Completion connection '{connection.Id}' has unsupported reasoningEffort value '{connection.ReasoningEffort}'."
                );
            }
            if (connection.MaxTokens is <= 0) {
                throw new InvalidOperationException(
                    $"Completion connection '{connection.Id}' MaxTokens must be null or positive."
                );
            }
            CompletionConnectionConfigValidation
                .ValidateAnthropicPromptCacheTtl(connection);

            resolvedConnections.Add(connection with { CompletionSurfaceId = completionSurfaceId, BaseAddress = baseAddress, ApiKey = apiKey });
        }

        string defaultConnectionId = !string.IsNullOrWhiteSpace(config.DefaultConnectionId)
            ? config.DefaultConnectionId!
            : resolvedConnections[0].Id;

        RequireConfigBound(
            defaultConnectionId,
            CompletionConnectionsManifestV1Reader.MaximumIdentifierUtf8Bytes,
            "Completion defaultConnectionId"
        );

        if (!connectionIds.Contains(defaultConnectionId)) { throw new InvalidOperationException($"Completion defaultConnectionId '{defaultConnectionId}' does not match any connection id."); }

        IReadOnlyList<string>? selectableConnectionIds =
            NormalizeSelectableConnectionIds(
                config.SelectableConnectionIds,
                connectionIds,
                defaultConnectionId
            );
        IReadOnlyDictionary<string, string?>? bindings =
            NormalizeBindings(config.Bindings, connectionIds);

        return CompletionConnectionsManifestV1Reader.Freeze(
            new CompletionConnectionsFileConfig(
                resolvedConnections,
                defaultConnectionId,
                selectableConnectionIds,
                bindings
            )
        );
    }

    private static IReadOnlyList<string>? NormalizeSelectableConnectionIds(
        IReadOnlyList<string>? configured,
        IReadOnlySet<string> connectionIds,
        string defaultConnectionId
    ) {
        if (configured is null) { return null; }
        if (configured.Count is < 1
            or > CompletionConnectionsManifestV1Reader
                .MaximumConnectionCount) {
            throw new InvalidOperationException(
                "Completion selectableConnectionIds must contain between "
                + "1 and 256 connection ids."
            );
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(configured.Count);
        for (int index = 0; index < configured.Count; index++) {
            string? connectionId = configured[index];
            RequireNonBlank(
                connectionId,
                $"Completion selectableConnectionIds[{index}] must be non-empty."
            );
            RequireConfigBound(
                connectionId!,
                CompletionConnectionsManifestV1Reader
                    .MaximumIdentifierUtf8Bytes,
                "Completion selectable connection id"
            );
            if (!seen.Add(connectionId!)) {
                throw new InvalidOperationException(
                    "Completion selectableConnectionIds contains duplicate "
                    + $"id '{connectionId}'."
                );
            }
            if (!connectionIds.Contains(connectionId!)) {
                throw new InvalidOperationException(
                    "Completion selectableConnectionIds references unknown "
                    + $"connection id '{connectionId}'."
                );
            }
            normalized.Add(connectionId!);
        }
        if (!seen.Contains(defaultConnectionId)) {
            throw new InvalidOperationException(
                "Completion selectableConnectionIds must contain the "
                + $"default connection id '{defaultConnectionId}'."
            );
        }
        return normalized;
    }

    private static IReadOnlyDictionary<string, string?>? NormalizeBindings(
        IReadOnlyDictionary<string, string?>? configured,
        IReadOnlySet<string> connectionIds
    ) {
        if (configured is null) { return null; }
        if (configured.Count
            > CompletionConnectionsManifestV1Reader
                .MaximumConnectionCount) {
            throw new InvalidOperationException(
                "Completion bindings must contain at most 256 entries."
            );
        }

        var normalized = new Dictionary<string, string?>(
            configured.Count,
            StringComparer.Ordinal
        );
        foreach ((string? binding, string? connectionId) in configured) {
            RequireNonBlank(
                binding,
                "Completion binding keys must be non-empty."
            );
            RequireConfigBound(
                binding!,
                CompletionConnectionsManifestV1Reader
                    .MaximumIdentifierUtf8Bytes,
                "Completion binding key"
            );
            if (!normalized.TryAdd(binding!, connectionId)) {
                throw new InvalidOperationException(
                    $"Completion bindings contains duplicate key '{binding}'."
                );
            }
            if (connectionId is null) { continue; }

            RequireNonBlank(
                connectionId,
                $"Completion binding '{binding}' must reference a non-empty connection id or null."
            );
            RequireConfigBound(
                connectionId,
                CompletionConnectionsManifestV1Reader
                    .MaximumIdentifierUtf8Bytes,
                "Completion binding connection id"
            );
            if (!connectionIds.Contains(connectionId)) {
                throw new InvalidOperationException(
                    $"Completion binding '{binding}' references unknown "
                    + $"connection id '{connectionId}'."
                );
            }
        }
        return normalized;
    }

    private static void RequireNonBlank(string? value, string message) {
        if (string.IsNullOrWhiteSpace(value)) { throw new InvalidOperationException(message); }
    }

    private static void RequireConfigBound(
        string value,
        int maximumUtf8Bytes,
        string field
    ) => CompletionConnectionsManifestV1Reader.RequireUtf8Bounded(
        value,
        maximumUtf8Bytes,
        field
    );

    private static string? ResolveEnvironmentVariable(
        string locator,
        string field
    ) {
        try {
            return Environment.GetEnvironmentVariable(locator);
        }
        catch (ArgumentException exception) {
            throw new InvalidOperationException(
                $"Completion connection {field} is not a usable environment locator.",
                exception
            );
        }
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

public sealed class CompletionConnectionRegistry : IDisposable,
    IAsyncDisposable {
    private readonly ICompletionClientFactory _factory;
    private readonly IReadOnlyDictionary<string, CompletionConnectionConfig> _byId;
    private readonly object _clientGate = new();
    private readonly Dictionary<string, ICompletionClient> _clients =
        new(StringComparer.Ordinal);
    private bool _disposed;

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
        lock (_clientGate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.TryGetValue(connection.Id, out var existing)) {
                return existing;
            }
            ICompletionClient created = _factory.Create(connection)
                ?? throw new InvalidOperationException(
                    $"Completion client factory returned null for '{connection.Id}'."
                );
            _clients.Add(connection.Id, created);
            return created;
        }
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
        var failures = new List<Exception>();
        foreach (ICompletionClient client in BeginDispose()) {
            try {
                if (client is IDisposable disposable) {
                    disposable.Dispose();
                }
                else if (client is IAsyncDisposable asyncDisposable) {
                    asyncDisposable.DisposeAsync().AsTask()
                        .GetAwaiter().GetResult();
                }
            }
            catch (Exception exception) when (!IsFatal(exception)) {
                failures.Add(exception);
            }
        }
        ThrowDisposeFailures(failures);
    }

    public async ValueTask DisposeAsync() {
        var failures = new List<Exception>();
        foreach (ICompletionClient client in BeginDispose()) {
            try {
                if (client is IAsyncDisposable asyncDisposable) {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (client is IDisposable disposable) {
                    disposable.Dispose();
                }
            }
            catch (Exception exception) when (!IsFatal(exception)) {
                failures.Add(exception);
            }
        }
        ThrowDisposeFailures(failures);
    }

    private ICompletionClient[] BeginDispose() {
        lock (_clientGate) {
            if (_disposed) {
                return [];
            }
            _disposed = true;
            ICompletionClient[] distinct = [.. _clients.Values.Distinct<
                ICompletionClient>(
                ReferenceEqualityComparer.Instance
            )];
            _clients.Clear();
            return distinct;
        }
    }

    private static void ThrowDisposeFailures(List<Exception> failures) {
        if (failures.Count == 1) {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1) {
            throw new AggregateException(
                "Multiple Completion clients failed during disposal.",
                failures
            );
        }
    }

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;
}
