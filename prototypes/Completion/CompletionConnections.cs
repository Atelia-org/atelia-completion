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
    string DisplayName,
    string Kind,
    string ModelId,
    string CompletionSurfaceId,
    string BaseAddress,
    string? ApiKey = null,
    string? BaseAddressEnv = null,
    string? ApiKeyEnv = null
);

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

            RequireNonBlank(connection.DisplayName, $"Completion connection '{connection.Id}' must have a non-empty displayName.");
            RequireNonBlank(connection.Kind, $"Completion connection '{connection.Id}' must have a non-empty kind.");
            RequireNonBlank(connection.ModelId, $"Completion connection '{connection.Id}' must have a non-empty modelId.");
            RequireNonBlank(connection.CompletionSurfaceId, $"Completion connection '{connection.Id}' must have a non-empty completionSurfaceId.");

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

            resolvedConnections.Add(connection with { BaseAddress = baseAddress, ApiKey = apiKey });
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
}

public interface ICompletionClientFactory {
    ICompletionClient Create(CompletionConnectionConfig connection);
}

public sealed class DefaultCompletionClientFactory : ICompletionClientFactory {
    public ICompletionClient Create(CompletionConnectionConfig connection) {
        ArgumentNullException.ThrowIfNull(connection);

        var httpClient = CompletionHttpTransportFactory.CreateLiveClient(new Uri(connection.BaseAddress, UriKind.Absolute));
        try {
            ICompletionClient client = connection.Kind.Trim().ToLowerInvariant() switch {
                "openai-chat" => new OpenAIChatClient(
                    apiKey: connection.ApiKey,
                    httpClient: httpClient,
                    dialect: ResolveOpenAiChatDialect(connection.CompletionSurfaceId)
                ),
                "openai-responses" => new OpenAIResponsesClient(
                    apiKey: connection.ApiKey,
                    httpClient: httpClient
                ),
                "anthropic" => new AnthropicClient(
                    apiKey: connection.ApiKey,
                    httpClient: httpClient
                ),
                _ => throw new InvalidOperationException($"Unsupported completion connection kind '{connection.Kind}'.")
            };

            return new OwnedHttpCompletionClient(client, httpClient);
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
            "openai-chat/deepseek-v4" => OpenAIChatDialects.DeepSeekV4,
            _ => OpenAIChatDialects.Strict
        };
    }
}

internal sealed class OwnedHttpCompletionClient : ICompletionClient, IDisposable {
    private readonly ICompletionClient _inner;
    private readonly HttpClient _httpClient;

    public OwnedHttpCompletionClient(ICompletionClient inner, HttpClient httpClient) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string Name => _inner.Name;

    public string ApiSpecId => _inner.ApiSpecId;

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => _inner.StreamCompletionAsync(request, observer, cancellationToken);

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
