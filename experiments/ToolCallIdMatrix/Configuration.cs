using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;

namespace ToolCallIdMatrix;

sealed record MatrixConfig {
    public required int Version { get; init; }
    public int RequestTimeoutSeconds { get; init; } = 120;
    public int MaxRequests { get; init; } = 40;
    public string OutputDirectory { get; init; } = "results";
    public required List<Endpoint> Endpoints { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static MatrixConfig Parse(string text) {
        if (text.Length > 1024 * 1024) throw new InvalidDataException("Config exceeds 1 MiB.");
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(doc.RootElement);
        RequireFields(doc.RootElement, ["version", "requestTimeoutSeconds", "maxRequests", "outputDirectory", "endpoints"]);
        if (doc.RootElement.TryGetProperty("endpoints", out var endpointArray) && endpointArray.ValueKind == JsonValueKind.Array)
            foreach (var endpoint in endpointArray.EnumerateArray())
                RequireFields(endpoint, ["id", "kind", "modelId", "completionSurfaceId", "baseAddress", "baseAddressEnv", "apiKeyEnv",
                    "reasoningEffort", "generationToolChoice", "codexAuthFile", "codexAccountFingerprintEnv"]);
        var value = JsonSerializer.Deserialize<MatrixConfig>(text, Json) ?? throw new InvalidDataException("Missing config.");
        if (value.Version != 1 || value.RequestTimeoutSeconds is < 1 or > 3600 || value.MaxRequests is < 1 or > 10000)
            throw new InvalidDataException("Invalid version, timeout, or request budget.");
        if (string.IsNullOrWhiteSpace(value.OutputDirectory) || value.Endpoints is not { Count: >= 1 and <= 32 })
            throw new InvalidDataException("Provide outputDirectory and 1..32 endpoints.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in value.Endpoints) {
            endpoint.Validate();
            if (!ids.Add(endpoint.Id)) throw new InvalidDataException("Duplicate endpoint id.");
        }
        return value;
    }

    static void RequireFields(JsonElement node, string[] allowed) {
        if (node.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected JSON object.");
        foreach (var property in node.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new InvalidDataException("Unknown JSON property.");
    }

    static void RejectDuplicates(JsonElement node) {
        if (node.ValueKind == JsonValueKind.Object) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject()) {
                if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) RejectDuplicates(item);
    }
}

sealed record Endpoint {
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string ModelId { get; init; }
    public string? CompletionSurfaceId { get; init; }
    public string? BaseAddress { get; init; }
    public string? BaseAddressEnv { get; init; }
    public string? ApiKeyEnv { get; init; }
    public CompletionReasoningEffort ReasoningEffort { get; init; } = CompletionReasoningEffort.ProviderDefault;
    public string GenerationToolChoice { get; init; } = "provider-default";
    public string? CodexAuthFile { get; init; }
    public string? CodexAccountFingerprintEnv { get; init; }
    public bool IsCodex => Kind == CodexSubscriptionCompletionClientFactory.ConnectionKind;

    public void Validate() {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidDataException("Endpoint id must be 1..128 ASCII letters, digits, '-' or '_'.");
        if (string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 256) throw new InvalidDataException("Invalid model id.");
        if (Kind is not ("openai-chat" or "openai-responses" or "anthropic" or "gemini" or "openai-codex-responses"))
            throw new InvalidDataException("Unsupported client kind.");
        if (GenerationToolChoice is not ("provider-default" or "required-any" or "required-named"))
            throw new InvalidDataException("Unsupported generationToolChoice.");
        if (IsCodex) {
            if (BaseAddress is not null || BaseAddressEnv is not null || ApiKeyEnv is not null || CompletionSurfaceId is not null)
                throw new InvalidDataException("Codex uses its pinned endpoint/surface; do not set base/key/surface.");
            if (CodexAuthFile is not null && !Path.IsPathFullyQualified(CodexAuthFile))
                throw new InvalidDataException("Codex auth file must be absolute.");
        }
        else {
            if (CodexAuthFile is not null || CodexAccountFingerprintEnv is not null)
                throw new InvalidDataException("Codex authentication is only valid for Codex.");
            if ((BaseAddress is null) == (BaseAddressEnv is null)) throw new InvalidDataException("Set exactly one baseAddress/baseAddressEnv.");
            if (BaseAddress is not null) CheckUri(BaseAddress);
            if (Kind == "openai-chat" && Surface is not ("openai-chat/strict" or "openai-chat/sglang-compatible" or "openai-chat/qwen-sglang" or "openai-chat/deepseek-v4"))
                throw new InvalidDataException("Unsupported Chat surface.");
            if (Kind != "openai-chat" && Surface != Kind) throw new InvalidDataException("Invalid surface for client kind.");
        }
        foreach (var name in new[] { ApiKeyEnv, BaseAddressEnv, CodexAccountFingerprintEnv })
            if (name is not null && (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
                throw new InvalidDataException("Environment names must contain ASCII letters/digits/underscore.");
    }

    public string Surface => CompletionSurfaceId ?? (Kind == "openai-chat" ? "openai-chat/strict" : Kind);

    public CompletionConnectionConfig Resolve() {
        var item = new CompletionConnectionConfig(Id, Kind, ModelId, Surface, BaseAddress ?? "", BaseAddressEnv: BaseAddressEnv,
            ApiKeyEnv: ApiKeyEnv, ReasoningEffort: ReasoningEffort);
        var result = CompletionConnectionConfigLoader.NormalizeAndValidateCatalog(new([item])).Connections[0];
        CheckUri(result.BaseAddress);
        return result;
    }

    static void CheckUri(string text) {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Base address must be HTTP(S), without credentials/query/fragment.");
    }
}
