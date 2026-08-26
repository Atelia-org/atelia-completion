using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;

namespace Atelia.Completion;

internal static class CompletionConnectionsManifestV1Reader {
    internal const int MaximumConnectionCount = 256;
    internal const int MaximumIdentifierUtf8Bytes = 128;
    internal const int MaximumEndpointUtf8Bytes = 4 * 1024;
    internal const int MaximumSecretUtf8Bytes = 64 * 1024;

    private const int MaximumDepth = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static CompletionConnectionsFileConfig Decode(
        ReadOnlySpan<byte> bytes
    ) {
        WireManifest manifest = Parse(bytes);
        var unresolved = new CompletionConnectionConfig[
            manifest.Connections.Count
        ];
        for (int index = 0; index < unresolved.Length; index++) {
            WireConnection item = manifest.Connections[index];
            if ((item.BaseAddress is null) == (item.BaseAddressEnv is null)) {
                throw new InvalidDataException(
                    "A connection must contain exactly one of baseAddress or baseAddressEnv."
                );
            }
            if (item.ApiKey is not null && item.ApiKeyEnv is not null) {
                throw new InvalidDataException(
                    "A connection must contain at most one of apiKey or apiKeyEnv."
                );
            }
            if (item.AnthropicPromptCacheTtl
                    is not AnthropicPromptCacheTtl.ProviderDefault
                && !string.Equals(
                    item.Kind,
                    "anthropic",
                    StringComparison.OrdinalIgnoreCase
                )) {
                throw new InvalidDataException(
                    "anthropicPromptCacheTtl is only valid for an anthropic connection."
                );
            }
            unresolved[index] = new CompletionConnectionConfig(
                item.Id,
                item.Kind,
                item.ModelId,
                item.CompletionSurfaceId,
                item.BaseAddress ?? string.Empty,
                item.ApiKey,
                item.BaseAddressEnv,
                item.ApiKeyEnv,
                item.MaxTokens,
                item.ReasoningEffort,
                item.AnthropicPromptCacheTtl
            );
        }

        CompletionConnectionsFileConfig normalized =
            CompletionConnectionConfigLoader.NormalizeAndValidate(
                new CompletionConnectionsFileConfig(
                    Array.AsReadOnly(unresolved),
                    manifest.DefaultConnectionId,
                    manifest.SelectableConnectionIds,
                    manifest.Bindings
                )
            );
        return Freeze(normalized);
    }

    internal static CompletionConnectionsFileConfig Freeze(
        CompletionConnectionsFileConfig config
    ) {
        CompletionConnectionConfig[] connections = config.Connections
            .ToArray();
        IReadOnlyList<string>? selectableConnectionIds =
            config.SelectableConnectionIds is null
                ? null
                : Array.AsReadOnly(
                    config.SelectableConnectionIds.ToArray()
                );
        IReadOnlyDictionary<string, string?>? bindings =
            config.Bindings is null
                ? null
                : new ReadOnlyDictionary<string, string?>(
                    new Dictionary<string, string?>(
                        config.Bindings,
                        StringComparer.Ordinal
                    )
                );
        return new CompletionConnectionsFileConfig(
            Array.AsReadOnly(connections),
            config.DefaultConnectionId,
            selectableConnectionIds,
            bindings
        );
    }

    internal static void RequireUtf8Bounded(
        string value,
        int maximumUtf8Bytes,
        string field
    ) {
        try {
            if (StrictUtf8.GetByteCount(value) > maximumUtf8Bytes) {
                throw new InvalidOperationException(
                    $"{field} exceeds its UTF-8 byte bound."
                );
            }
        }
        catch (EncoderFallbackException exception) {
            throw new InvalidOperationException(
                $"{field} contains invalid Unicode text.",
                exception
            );
        }
    }

    private static WireManifest Parse(ReadOnlySpan<byte> bytes) {
        if (bytes.Length is < 1
            or > CompletionConnectionConfigLoader.MaximumInputUtf8Bytes) {
            throw new InvalidDataException(
                "Completion connections bytes are empty or exceed the 1 MiB V1 bound."
            );
        }
        if (bytes.Length >= 3
            && bytes[0] == 0xef
            && bytes[1] == 0xbb
            && bytes[2] == 0xbf) {
            throw new InvalidDataException(
                "Completion connections bytes must not contain a UTF-8 BOM."
            );
        }

        try {
            _ = StrictUtf8.GetString(bytes);
            using JsonDocument document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumDepth
                }
            );
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("v", out _)) {
                throw new InvalidDataException(
                    "Completion connections require exact integer version 'v': 1; migrate the manifest before retrying."
                );
            }
            RequireProperties(
                root,
                required: ["v", "connections", "defaultConnectionId"],
                optional: ["selectableConnectionIds", "bindings"]
            );
            JsonElement version = root.GetProperty("v");
            if (version.ValueKind is not JsonValueKind.Number
                || !string.Equals(
                    version.GetRawText(),
                    "1",
                    StringComparison.Ordinal
                )) {
                throw new InvalidDataException(
                    "Completion connections require exact integer version 'v': 1; migrate the manifest before retrying."
                );
            }

            JsonElement array = root.GetProperty("connections");
            if (array.ValueKind is not JsonValueKind.Array
                || array.GetArrayLength() is < 1 or > MaximumConnectionCount) {
                throw new InvalidDataException(
                    "Completion connections count must be between 1 and 256."
                );
            }
            var connections = new List<WireConnection>(
                array.GetArrayLength()
            );
            foreach (JsonElement item in array.EnumerateArray()) {
                connections.Add(ParseConnection(item));
            }
            string defaultConnectionId = RequireString(
                root,
                "defaultConnectionId",
                MaximumIdentifierUtf8Bytes
            );
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (WireConnection item in connections) {
                if (!ids.Add(item.Id)) {
                    throw new InvalidDataException(
                        "Completion connections contain a duplicate id."
                    );
                }
            }
            if (!ids.Contains(defaultConnectionId)) {
                throw new InvalidDataException(
                    "defaultConnectionId must exactly match one connection id."
                );
            }
            IReadOnlyList<string>? selectableConnectionIds =
                ParseSelectableConnectionIds(
                    root,
                    ids,
                    defaultConnectionId
                );
            IReadOnlyDictionary<string, string?>? bindings =
                ParseBindings(root, ids);
            return new WireManifest(
                connections,
                defaultConnectionId,
                selectableConnectionIds,
                bindings
            );
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or DecoderFallbackException
                or ArgumentException
                or FormatException
                or InvalidOperationException
                or OverflowException) {
            throw new InvalidDataException(
                "Completion connections are not a strict bounded V1 document.",
                exception
            );
        }
    }

    private static IReadOnlyList<string>? ParseSelectableConnectionIds(
        JsonElement root,
        IReadOnlySet<string> connectionIds,
        string defaultConnectionId
    ) {
        if (!root.TryGetProperty(
                "selectableConnectionIds",
                out JsonElement array)) {
            return null;
        }
        if (array.ValueKind is not JsonValueKind.Array
            || array.GetArrayLength() is < 1 or > MaximumConnectionCount) {
            throw new InvalidDataException(
                "selectableConnectionIds must contain between 1 and 256 connection ids."
            );
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(array.GetArrayLength());
        foreach (JsonElement item in array.EnumerateArray()) {
            string connectionId = RequireBoundedString(
                item,
                "selectableConnectionIds item",
                MaximumIdentifierUtf8Bytes
            );
            if (!seen.Add(connectionId)) {
                throw new InvalidDataException(
                    "selectableConnectionIds contains a duplicate connection id."
                );
            }
            if (!connectionIds.Contains(connectionId)) {
                throw new InvalidDataException(
                    "selectableConnectionIds references an unknown connection id."
                );
            }
            result.Add(connectionId);
        }
        if (!seen.Contains(defaultConnectionId)) {
            throw new InvalidDataException(
                "selectableConnectionIds must contain defaultConnectionId."
            );
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string?>? ParseBindings(
        JsonElement root,
        IReadOnlySet<string> connectionIds
    ) {
        if (!root.TryGetProperty("bindings", out JsonElement bindings)) {
            return null;
        }
        if (bindings.ValueKind is not JsonValueKind.Object) {
            throw new InvalidDataException("bindings must be an object.");
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (JsonProperty property in bindings.EnumerateObject()) {
            if (result.Count >= MaximumConnectionCount) {
                throw new InvalidDataException(
                    "bindings must contain at most 256 entries."
                );
            }
            string binding = RequireBoundedText(
                property.Name,
                "binding key",
                MaximumIdentifierUtf8Bytes
            );
            if (result.ContainsKey(binding)) {
                throw new InvalidDataException(
                    "bindings contains a duplicate key."
                );
            }

            string? connectionId = property.Value.ValueKind switch {
                JsonValueKind.Null => null,
                JsonValueKind.String => RequireBoundedString(
                    property.Value,
                    "binding connection id",
                    MaximumIdentifierUtf8Bytes
                ),
                _ => throw new InvalidDataException(
                    "binding values must be a connection id string or null."
                )
            };
            if (connectionId is not null
                && !connectionIds.Contains(connectionId)) {
                throw new InvalidDataException(
                    "bindings references an unknown connection id."
                );
            }
            result.Add(binding, connectionId);
        }
        return result;
    }

    private static WireConnection ParseConnection(JsonElement item) {
        RequireProperties(
            item,
            required: ["id", "kind", "modelId", "completionSurfaceId"],
            optional: [
                "baseAddress", "baseAddressEnv", "apiKey", "apiKeyEnv",
                "maxTokens", "reasoningEffort", "anthropicPromptCacheTtl"
            ]
        );
        return new WireConnection(
            RequireString(item, "id", MaximumIdentifierUtf8Bytes),
            RequireString(item, "kind", MaximumIdentifierUtf8Bytes),
            RequireString(item, "modelId", MaximumIdentifierUtf8Bytes),
            RequireString(
                item,
                "completionSurfaceId",
                MaximumIdentifierUtf8Bytes
            ),
            OptionalPresentString(
                item,
                "baseAddress",
                MaximumEndpointUtf8Bytes
            ),
            OptionalPresentString(
                item,
                "apiKey",
                MaximumSecretUtf8Bytes
            ),
            OptionalPresentString(
                item,
                "baseAddressEnv",
                MaximumIdentifierUtf8Bytes
            ),
            OptionalPresentString(
                item,
                "apiKeyEnv",
                MaximumIdentifierUtf8Bytes
            ),
            OptionalPositivePlainInt32(item, "maxTokens"),
            OptionalReasoningEffort(item),
            OptionalAnthropicPromptCacheTtl(item)
        );
    }

    private static int? OptionalPositivePlainInt32(
        JsonElement item,
        string propertyName
    ) {
        if (!item.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind is JsonValueKind.Null) {
            return null;
        }
        string raw = value.GetRawText();
        if (value.ValueKind is not JsonValueKind.Number
            || !int.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed
            )
            || parsed <= 0) {
            throw new InvalidDataException(
                "maxTokens must be null or a positive plain Int32."
            );
        }
        return parsed;
    }

    private static CompletionReasoningEffort OptionalReasoningEffort(
        JsonElement item
    ) => !item.TryGetProperty("reasoningEffort", out JsonElement value)
        ? CompletionReasoningEffort.ProviderDefault
        : RequireStringValue(value, "reasoningEffort") switch {
            "provider-default" => CompletionReasoningEffort.ProviderDefault,
            "disabled" => CompletionReasoningEffort.Disabled,
            "low" => CompletionReasoningEffort.Low,
            "medium" => CompletionReasoningEffort.Medium,
            "high" => CompletionReasoningEffort.High,
            "max" => CompletionReasoningEffort.Max,
            _ => throw new InvalidDataException(
                "reasoningEffort is unsupported."
            )
        };

    private static AnthropicPromptCacheTtl OptionalAnthropicPromptCacheTtl(
        JsonElement item
    ) => !item.TryGetProperty(
            "anthropicPromptCacheTtl",
            out JsonElement value
        )
        ? AnthropicPromptCacheTtl.ProviderDefault
        : RequireStringValue(value, "anthropicPromptCacheTtl") switch {
            "provider-default" => AnthropicPromptCacheTtl.ProviderDefault,
            "5m" => AnthropicPromptCacheTtl.FiveMinutes,
            "1h" => AnthropicPromptCacheTtl.OneHour,
            _ => throw new InvalidDataException(
                "anthropicPromptCacheTtl is unsupported."
            )
        };

    private static string RequireString(
        JsonElement item,
        string propertyName,
        int maximumUtf8Bytes
    ) {
        if (!item.TryGetProperty(propertyName, out JsonElement value)) {
            throw new InvalidDataException(
                $"Required property '{propertyName}' is absent."
            );
        }
        return RequireBoundedString(value, propertyName, maximumUtf8Bytes);
    }

    private static string? OptionalPresentString(
        JsonElement item,
        string propertyName,
        int maximumUtf8Bytes
    ) => !item.TryGetProperty(propertyName, out JsonElement value)
        ? null
        : RequireBoundedString(value, propertyName, maximumUtf8Bytes);

    private static string RequireBoundedString(
        JsonElement value,
        string propertyName,
        int maximumUtf8Bytes
    ) {
        string parsed = RequireStringValue(value, propertyName);
        return RequireBoundedText(
            parsed,
            propertyName,
            maximumUtf8Bytes
        );
    }

    private static string RequireBoundedText(
        string parsed,
        string propertyName,
        int maximumUtf8Bytes
    ) {
        if (string.IsNullOrWhiteSpace(parsed)
            || StrictUtf8.GetByteCount(parsed) > maximumUtf8Bytes) {
            throw new InvalidDataException(
                $"{propertyName} must be nonblank and within its UTF-8 byte bound."
            );
        }
        return parsed;
    }

    private static string RequireStringValue(
        JsonElement value,
        string propertyName
    ) => value.ValueKind is JsonValueKind.String
        ? value.GetString() ?? throw new InvalidDataException(
            $"{propertyName} must not be null."
        )
        : throw new InvalidDataException(
            $"{propertyName} must be a string."
        );

    private static void RequireProperties(
        JsonElement value,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional
    ) {
        if (value.ValueKind is not JsonValueKind.Object) {
            throw new InvalidDataException("A JSON object is required.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject()) {
            if (!seen.Add(property.Name)
                || (!required.Contains(property.Name, StringComparer.Ordinal)
                    && !optional.Contains(
                        property.Name,
                        StringComparer.Ordinal
                    ))) {
                throw new InvalidDataException(
                    "Completion connections contain an unknown or duplicate property."
                );
            }
        }
        if (required.Any(property => !seen.Contains(property))) {
            throw new InvalidDataException(
                "Completion connections are missing a required property."
            );
        }
    }

    private sealed record WireManifest(
        IReadOnlyList<WireConnection> Connections,
        string DefaultConnectionId,
        IReadOnlyList<string>? SelectableConnectionIds,
        IReadOnlyDictionary<string, string?>? Bindings
    );

    private sealed record WireConnection(
        string Id,
        string Kind,
        string ModelId,
        string CompletionSurfaceId,
        string? BaseAddress,
        string? ApiKey,
        string? BaseAddressEnv,
        string? ApiKeyEnv,
        int? MaxTokens,
        CompletionReasoningEffort ReasoningEffort,
        AnthropicPromptCacheTtl AnthropicPromptCacheTtl
    );
}
