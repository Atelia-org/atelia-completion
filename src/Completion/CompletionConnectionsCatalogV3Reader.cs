using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace Atelia.Completion;

internal static class CompletionConnectionsCatalogV3Reader {
    private const int MaximumDepth = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static CompletionConnectionCatalogConfig Decode(
        ReadOnlySpan<byte> bytes
    ) {
        if (bytes.Length is < 1
            or > CompletionConnectionConfigLoader.MaximumInputUtf8Bytes) {
            throw new InvalidDataException(
                "Completion catalog bytes are empty or exceed the 1 MiB V3 bound."
            );
        }
        if (bytes.Length >= 3
            && bytes[0] == 0xef
            && bytes[1] == 0xbb
            && bytes[2] == 0xbf) {
            throw new InvalidDataException(
                "Completion catalog bytes must not contain a UTF-8 BOM."
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
                throw UnsupportedVersion();
            }
            CompletionConnectionsManifestV2Reader.RequireProperties(
                root,
                required: ["v", "connections"],
                optional: ["selectableConnectionIds", "bindings"]
            );
            JsonElement version = root.GetProperty("v");
            if (version.ValueKind is not JsonValueKind.Number
                || !string.Equals(
                    version.GetRawText(),
                    "3",
                    StringComparison.Ordinal
                )) {
                throw UnsupportedVersion();
            }

            JsonElement array = root.GetProperty("connections");
            if (array.ValueKind is not JsonValueKind.Array
                || array.GetArrayLength() is < 1
                    or > CompletionConnectionsManifestV2Reader
                        .MaximumConnectionCount) {
                throw new InvalidDataException(
                    "Completion connections count must be between 1 and 256."
                );
            }
            var connectionIds = new HashSet<string>(StringComparer.Ordinal);
            var connections = new CompletionConnectionConfig[
                array.GetArrayLength()
            ];
            int index = 0;
            foreach (JsonElement item in array.EnumerateArray()) {
                CompletionConnectionsManifestV2Reader.WireConnection parsed =
                    CompletionConnectionsManifestV2Reader.ParseConnection(item);
                if (!connectionIds.Add(parsed.Id)) {
                    throw new InvalidDataException(
                        "Completion connections contain a duplicate id."
                    );
                }
                connections[index++] = CompletionConnectionsManifestV2Reader
                    .MaterializeConnection(parsed);
            }

            IReadOnlyList<string>? selectableConnectionIds =
                CompletionConnectionsManifestV2Reader
                    .ParseSelectableConnectionIds(
                        root,
                        connectionIds,
                        defaultConnectionId: null
                    );
            IReadOnlyDictionary<string, string?>? bindings =
                CompletionConnectionsManifestV2Reader.ParseBindings(
                    root,
                    connectionIds
                );
            return CompletionConnectionConfigLoader.NormalizeAndValidateCatalog(
                new CompletionConnectionCatalogConfig(
                    Array.AsReadOnly(connections),
                    selectableConnectionIds,
                    bindings
                )
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
                or OverflowException) {
            throw new InvalidDataException(
                "Completion catalog is not a strict bounded V3 document.",
                exception
            );
        }
    }

    internal static CompletionConnectionCatalogConfig Freeze(
        CompletionConnectionCatalogConfig config
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
        return new CompletionConnectionCatalogConfig(
            Array.AsReadOnly(connections),
            selectableConnectionIds,
            bindings
        );
    }

    private static InvalidDataException UnsupportedVersion() => new(
        "Completion catalog requires exact integer version 'v': 3; "
        + "migrate the manifest before retrying."
    );
}
