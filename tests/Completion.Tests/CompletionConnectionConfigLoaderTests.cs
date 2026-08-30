using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class CompletionConnectionConfigLoaderTests {
    [Fact]
    public void Decode_AcceptsStrictV1AndFreezesResult() {
        CompletionConnectionsFileConfig config = Decode(
            Connection(
                kind: "anthropic",
                surface: "anthropic",
                extra: "\"apiKey\":\"secret\",\"maxTokens\":2048,"
                    + "\"reasoningEffort\":\"high\","
                    + "\"anthropicPromptCacheTtl\":\"1h\""
            )
        );

        CompletionConnectionConfig item = Assert.Single(config.Connections);
        Assert.Equal("main", config.DefaultConnectionId);
        Assert.Equal("secret", item.ApiKey);
        Assert.Equal(2048, item.MaxTokens);
        Assert.Equal(CompletionReasoningEffort.High, item.ReasoningEffort);
        Assert.Equal(
            AnthropicPromptCacheTtl.OneHour,
            item.AnthropicPromptCacheTtl
        );
        Assert.Null(config.SelectableConnectionIds);
        Assert.Null(config.Bindings);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CompletionConnectionConfig>)config.Connections)[0] = item
        );
    }

    [Fact]
    public void Decode_AcceptsAndFreezesOptionalSelectionAndBindings() {
        CompletionConnectionsFileConfig config = DecodeDocument(Root(
            Connection() + "," + Connection(id: "helper"),
            extra: "\"selectableConnectionIds\":[\"helper\",\"main\"],"
                + "\"bindings\":{\"feature.primary\":\"helper\","
                + "\"feature.disabled\":null}"
        ));

        Assert.Equal(
            ["helper", "main"],
            config.SelectableConnectionIds
        );
        Assert.Equal(
            "helper",
            config.Bindings!["feature.primary"]
        );
        Assert.Null(config.Bindings["feature.disabled"]);
        Assert.Empty(DecodeDocument(Root(
            Connection(),
            extra: "\"bindings\":{}"
        )).Bindings!);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)config.SelectableConnectionIds!)[0] = "main"
        );
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string?>)config.Bindings)
                ["feature.other"] = "main"
        );
    }

    public static TheoryData<string> InvalidSelectableFields => new() {
        "null",
        "{}",
        "[]",
        "[1]",
        "[\" \u2003\"]",
        "[\"main\",\"main\"]",
        "[\"missing\",\"main\"]",
        "[\"MAIN\"]",
    };

    [Theory]
    [MemberData(nameof(InvalidSelectableFields))]
    public void Decode_RejectsInvalidSelectableConnectionIds(string value) {
        Assert.Throws<InvalidDataException>(() => DecodeDocument(Root(
            Connection(),
            extra: $"\"selectableConnectionIds\":{value}"
        )));
    }

    public static TheoryData<string> InvalidBindingFields => new() {
        "null",
        "[]",
        "{\"feature\":1}",
        "{\"\":\"main\"}",
        "{\"feature\":\"\"}",
        "{\"feature\":\"missing\"}",
        "{\"feature\":\"main\",\"feature\":null}",
    };

    [Theory]
    [MemberData(nameof(InvalidBindingFields))]
    public void Decode_RejectsInvalidBindings(string value) {
        Assert.Throws<InvalidDataException>(() => DecodeDocument(Root(
            Connection(),
            extra: $"\"bindings\":{value}"
        )));
    }

    [Fact]
    public void Decode_BindingKeysAreExactAndMayShareTargets() {
        CompletionConnectionsFileConfig config = DecodeDocument(Root(
            Connection(),
            extra: "\"bindings\":{\"Role\":\"main\","
                + "\"role\":\"main\"}"
        ));

        Assert.Equal(2, config.Bindings!.Count);
        Assert.Equal("main", config.Bindings["Role"]);
        Assert.Equal("main", config.Bindings["role"]);
    }

    [Fact]
    public void Decode_EnforcesOptionalFieldCountsAndUtf8Bounds() {
        string exact = new string('界', 42) + "aa";
        string tooLong = exact + "a";
        CompletionConnectionsFileConfig exactConfig = DecodeDocument(Root(
            Connection(id: exact),
            defaultId: exact,
            extra: $"\"selectableConnectionIds\":[\"{exact}\"],"
                + $"\"bindings\":{{\"{exact}\":\"{exact}\"}}"
        ));
        Assert.Equal(exact, Assert.Single(
            exactConfig.SelectableConnectionIds!
        ));
        Assert.Equal(exact, exactConfig.Bindings![exact]);

        Assert.Throws<InvalidDataException>(() => DecodeDocument(Root(
            Connection(),
            extra: $"\"selectableConnectionIds\":[\"main\",\"{tooLong}\"]"
        )));
        Assert.Throws<InvalidDataException>(() => DecodeDocument(Root(
            Connection(),
            extra: $"\"bindings\":{{\"{tooLong}\":null}}"
        )));
        Assert.Throws<InvalidDataException>(() => DecodeDocument(Root(
            Connection(),
            extra: $"\"bindings\":{{\"feature\":\"{tooLong}\"}}"
        )));

        string selectable = string.Join(
            ',',
            Enumerable.Repeat("\"main\"", 257)
        );
        Assert.Throws<InvalidDataException>(() => DecodeDocument(Root(
            Connection(),
            extra: $"\"selectableConnectionIds\":[{selectable}]"
        )));
        string bindings = string.Join(
            ',',
            Enumerable.Range(0, 257)
                .Select(index => $"\"feature.{index}\":null")
        );
        Assert.Throws<InvalidDataException>(() => DecodeDocument(Root(
            Connection(),
            extra: $"\"bindings\":{{{bindings}}}"
        )));
    }

    [Fact]
    public void Decode_DefaultsOptionalFieldsAndKeepsKindAndSurfaceOpen() {
        CompletionConnectionConfig item = Assert.Single(
            Decode(Connection(kind: "custom-kind", surface: "custom-v7"))
                .Connections
        );

        Assert.Null(item.MaxTokens);
        Assert.Equal(
            CompletionReasoningEffort.ProviderDefault,
            item.ReasoningEffort
        );
        Assert.Equal(
            AnthropicPromptCacheTtl.ProviderDefault,
            item.AnthropicPromptCacheTtl
        );
        Assert.Equal("custom-kind", item.Kind);
        Assert.Equal("custom-v7", item.CompletionSurfaceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"1\"")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    public void Decode_RequiresExactIntegerV1(string? version) {
        string document = version is null
            ? "{\"connections\":[" + Connection()
                + "],\"defaultConnectionId\":\"main\"}"
            : Root(Connection(), version);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => CompletionConnectionConfigLoader.Decode(
                Encoding.UTF8.GetBytes(document)
            )
        );
        Assert.Contains("migrate", exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<byte[]> StructuralFailures => new() {
        Encoding.UTF8.GetBytes(
            "{\"v\":1,\"v\":1,\"connections\":[],\"defaultConnectionId\":\"main\"}"
        ),
        Encoding.UTF8.GetBytes(
            "{\"v\":1,\"connections\":[],\"defaultConnectionId\":\"main\",\"unknown\":1}"
        ),
        Encoding.UTF8.GetBytes(
            "{\"V\":1,\"connections\":[],\"defaultConnectionId\":\"main\"}"
        ),
        Encoding.UTF8.GetBytes(Root(
            Connection(),
            extra: "\"selectableConnectionIds\":[\"main\"],"
                + "\"SelectableConnectionIds\":[\"main\"]"
        )),
        Encoding.UTF8.GetBytes(Root(
            Connection(),
            extra: "\"bindings\":{},\"bindings\":{}"
        )),
        Encoding.UTF8.GetBytes(Root(Connection()) + " trailing"),
        Encoding.UTF8.GetBytes(Root(Connection()).Replace("}", "},", StringComparison.Ordinal)),
        Encoding.UTF8.GetBytes("/*comment*/" + Root(Connection())),
        Encoding.UTF8.GetBytes(
            Root(Connection(id: "\\ud800"))
        ),
        new byte[] { 0xef, 0xbb, 0xbf }
            .Concat(Encoding.UTF8.GetBytes(Root(Connection())))
            .ToArray(),
        new byte[] { 0xff }
    };

    [Theory]
    [MemberData(nameof(StructuralFailures))]
    public void Decode_RejectsAmbiguousOrNonStrictBytes(byte[] bytes) {
        Assert.Throws<InvalidDataException>(() =>
            CompletionConnectionConfigLoader.Decode(bytes)
        );
    }

    [Fact]
    public void Decode_RejectsDepthBeyondEightDuringJsonParsing() {
        byte[] tooDeep = Encoding.UTF8.GetBytes(
            Root(Connection())[..^1]
                + ",\"unknown\":[[[[[[[[[0]]]]]]]]]}"
        );

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => CompletionConnectionConfigLoader.Decode(tooDeep)
        );

        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
        Assert.DoesNotContain(
            "unknown",
            exception.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Theory]
    [InlineData("{\"id\":\"main\",\"kind\":\"test\",\"modelId\":\"model\",\"completionSurfaceId\":\"test-v1\"}")]
    [InlineData("{\"id\":\"main\",\"kind\":\"test\",\"modelId\":\"model\",\"completionSurfaceId\":\"test-v1\",\"baseAddress\":\"a\",\"baseAddressEnv\":\"B\"}")]
    [InlineData("{\"id\":\"main\",\"kind\":\"test\",\"modelId\":\"model\",\"completionSurfaceId\":\"test-v1\",\"baseAddress\":null}")]
    [InlineData("{\"id\":\"main\",\"kind\":\"test\",\"modelId\":\"model\",\"completionSurfaceId\":\"test-v1\",\"baseAddress\":\"   \"}")]
    [InlineData("{\"id\":\"main\",\"kind\":\"test\",\"modelId\":\"model\",\"completionSurfaceId\":\"test-v1\",\"baseAddress\":\"a\",\"apiKey\":\"k\",\"apiKeyEnv\":\"K\"}")]
    [InlineData("{\"id\":\"main\",\"kind\":\"test\",\"modelId\":\"model\",\"completionSurfaceId\":\"test-v1\",\"baseAddress\":\"a\",\"apiKey\":null}")]
    [InlineData("{\"id\":\"main\",\"kind\":\"test\",\"modelId\":\"model\",\"completionSurfaceId\":\"test-v1\",\"baseAddress\":\"a\",\"apiKeyEnv\":\"\"}")]
    public void Decode_EnforcesWireSourceSyntax(string item) {
        Assert.Throws<InvalidDataException>(() => Decode(item));
    }

    [Fact]
    public void Decode_AllowsEnvironmentOnlySources() {
        string endpointEnv = EnvName("ENDPOINT");
        string keyEnv = EnvName("KEY");
        try {
            Environment.SetEnvironmentVariable(endpointEnv, "endpoint");
            Environment.SetEnvironmentVariable(keyEnv, "secret");
            string item = Connection(
                source: $"\"baseAddressEnv\":\"{endpointEnv}\"",
                extra: $"\"apiKeyEnv\":\"{keyEnv}\""
            );

            CompletionConnectionConfig decoded = Assert.Single(
                Decode(item).Connections
            );

            Assert.Equal("endpoint", decoded.BaseAddress);
            Assert.Equal("secret", decoded.ApiKey);
            Assert.Equal(endpointEnv, decoded.BaseAddressEnv);
            Assert.Equal(keyEnv, decoded.ApiKeyEnv);
        }
        finally {
            Environment.SetEnvironmentVariable(endpointEnv, null);
            Environment.SetEnvironmentVariable(keyEnv, null);
        }
    }

    [Fact]
    public void Decode_ReportsUnavailableEnvironmentAsInvalidOperation() {
        string endpointEnv = EnvName("MISSING_ENDPOINT");
        try {
            Environment.SetEnvironmentVariable(endpointEnv, null);
            Assert.Throws<InvalidOperationException>(() => Decode(Connection(
                source: $"\"baseAddressEnv\":\"{endpointEnv}\""
            )));

            Environment.SetEnvironmentVariable(endpointEnv, "   ");
            Assert.Throws<InvalidOperationException>(() => Decode(Connection(
                source: $"\"baseAddressEnv\":\"{endpointEnv}\""
            )));

            Assert.Throws<InvalidOperationException>(() => Decode(Connection(
                source: "\"baseAddressEnv\":\"invalid=locator\""
            )));
        }
        finally {
            Environment.SetEnvironmentVariable(endpointEnv, null);
        }
    }

    [Fact]
    public void Decode_RechecksResolvedEndpointAndSecretCapsWithoutLeakingValues() {
        string endpointEnv = EnvName("ENDPOINT_CAP");
        string keyEnv = EnvName("KEY_CAP");
        string endpoint = new('e', 4 * 1024 + 1);
        string secret = "DO_NOT_LEAK_" + new string('s', 64 * 1024);
        try {
            Environment.SetEnvironmentVariable(endpointEnv, endpoint);
            Environment.SetEnvironmentVariable(keyEnv, "key");
            InvalidOperationException endpointFailure = Assert.Throws<
                InvalidOperationException>(() => Decode(Connection(
                    source: $"\"baseAddressEnv\":\"{endpointEnv}\""
                )));
            Assert.DoesNotContain(endpoint, endpointFailure.ToString(),
                StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(endpointEnv, "endpoint");
            Environment.SetEnvironmentVariable(keyEnv, secret);
            InvalidOperationException secretFailure = Assert.Throws<
                InvalidOperationException>(() => Decode(Connection(
                    source: $"\"baseAddressEnv\":\"{endpointEnv}\"",
                    extra: $"\"apiKeyEnv\":\"{keyEnv}\""
                )));
            Assert.DoesNotContain("DO_NOT_LEAK_", secretFailure.ToString(),
                StringComparison.Ordinal);
        }
        finally {
            Environment.SetEnvironmentVariable(endpointEnv, null);
            Environment.SetEnvironmentVariable(keyEnv, null);
        }
    }

    [Fact]
    public void Decode_AcceptsResolvedValuesAtExactCaps() {
        string endpointEnv = EnvName("ENDPOINT_EXACT");
        string keyEnv = EnvName("KEY_EXACT");
        try {
            Environment.SetEnvironmentVariable(endpointEnv,
                new string('e', 4 * 1024));
            Environment.SetEnvironmentVariable(keyEnv,
                new string('s', 64 * 1024));

            CompletionConnectionConfig item = Assert.Single(Decode(Connection(
                source: $"\"baseAddressEnv\":\"{endpointEnv}\"",
                extra: $"\"apiKeyEnv\":\"{keyEnv}\""
            )).Connections);

            Assert.Equal(4 * 1024, Encoding.UTF8.GetByteCount(item.BaseAddress));
            Assert.Equal(64 * 1024,
                Encoding.UTF8.GetByteCount(item.ApiKey!));
        }
        finally {
            Environment.SetEnvironmentVariable(endpointEnv, null);
            Environment.SetEnvironmentVariable(keyEnv, null);
        }
    }

    [Fact]
    public void Decode_EnforcesCountAndInputByteBounds() {
        Assert.Throws<InvalidDataException>(() => DecodeMany(0));
        Assert.Single(DecodeMany(1).Connections);
        Assert.Equal(256, DecodeMany(256).Connections.Count);
        Assert.Throws<InvalidDataException>(() => DecodeMany(257));

        byte[] compact = Encoding.UTF8.GetBytes(Root(Connection()));
        byte[] exact = GC.AllocateUninitializedArray<byte>(
            CompletionConnectionConfigLoader.MaximumInputUtf8Bytes
        );
        compact.CopyTo(exact, 0);
        exact.AsSpan(compact.Length).Fill((byte)' ');
        Assert.Single(CompletionConnectionConfigLoader.Decode(exact)
            .Connections);
        Assert.Throws<InvalidDataException>(() =>
            CompletionConnectionConfigLoader.Decode(
                new byte[
                    CompletionConnectionConfigLoader.MaximumInputUtf8Bytes + 1
                ]
            )
        );
    }

    [Fact]
    public void Decode_EnforcesRequiredIdentityAndDefaultRules() {
        Assert.Throws<InvalidDataException>(() => Decode(
            Connection(id: " ")
        ));
        Assert.Throws<InvalidDataException>(() => Decode(
            Connection().Replace("\"kind\":\"test\",", string.Empty,
                StringComparison.Ordinal)
        ));
        Assert.Throws<InvalidDataException>(() => Decode(
            Connection().Replace("\"completionSurfaceId\":\"test-v1\",",
                string.Empty, StringComparison.Ordinal)
        ));
        Assert.Throws<InvalidDataException>(() => Decode(
            Connection(), defaultId: "MAIN"
        ));
        Assert.Throws<InvalidDataException>(() =>
            CompletionConnectionConfigLoader.Decode(Encoding.UTF8.GetBytes(
                "{\"v\":1,\"connections\":[" + Connection() + ","
                + Connection() + "],\"defaultConnectionId\":\"main\"}"
            ))
        );
    }

    [Fact]
    public void Decode_EnforcesUtf8FieldCapsIncludingMultibyteText() {
        string identifierExact = new string('界', 42) + "aa";
        string identifierTooLong = identifierExact + "a";
        Assert.Equal(128, Encoding.UTF8.GetByteCount(identifierExact));
        Assert.Single(Decode(Connection(id: identifierExact), identifierExact)
            .Connections);
        Assert.Throws<InvalidDataException>(() => Decode(
            Connection(id: identifierTooLong),
            identifierTooLong
        ));

        Assert.Single(Decode(Connection(
            source: "\"baseAddress\":\"" + new string('e', 4 * 1024)
                + "\"",
            extra: "\"apiKey\":\"" + new string('s', 64 * 1024) + "\""
        )).Connections);
        Assert.Throws<InvalidDataException>(() => Decode(Connection(
            source: "\"baseAddress\":\"" + new string('e', 4 * 1024 + 1)
                + "\""
        )));
        string secret = "DO_NOT_LEAK_" + new string('s', 64 * 1024);
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
            Decode(Connection(extra: "\"apiKey\":\"" + secret + "\""))
        );
        Assert.DoesNotContain("DO_NOT_LEAK_", failure.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("2147483647")]
    public void Decode_AcceptsSupportedMaxTokens(string token) {
        string extra = token.Length == 0
            ? string.Empty
            : $"\"maxTokens\":{token}";
        Assert.Single(Decode(Connection(extra: extra)).Connections);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("\"1\"")]
    public void Decode_RejectsUnsupportedMaxTokens(string token) {
        Assert.Throws<InvalidDataException>(() => Decode(Connection(
            extra: $"\"maxTokens\":{token}"
        )));
    }

    [Theory]
    [InlineData("provider-default")]
    [InlineData("disabled")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("max")]
    public void Decode_AcceptsExactReasoningNames(string value) {
        Assert.Single(Decode(Connection(
            extra: $"\"reasoningEffort\":\"{value}\""
        )).Connections);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"High\"")]
    [InlineData("\"turbo\"")]
    public void Decode_RejectsOtherReasoningTokens(string value) {
        Assert.Throws<InvalidDataException>(() => Decode(Connection(
            extra: $"\"reasoningEffort\":{value}"
        )));
    }

    [Theory]
    [InlineData("provider-default")]
    [InlineData("5m")]
    [InlineData("1h")]
    public void Decode_AcceptsExactAnthropicTtlNames(string value) {
        Assert.Single(Decode(Connection(
            kind: "anthropic",
            surface: "anthropic",
            extra: $"\"anthropicPromptCacheTtl\":\"{value}\""
        )).Connections);
    }

    [Fact]
    public void Decode_RejectsTtlForOtherKinds() {
        Assert.Throws<InvalidDataException>(() => Decode(Connection(
            extra: "\"anthropicPromptCacheTtl\":\"1h\""
        )));
    }

    [Fact]
    public void LoadFile_IsBoundedAndDelegatesToDecode() {
        string root = CreateTempDirectory();
        try {
            string path = Path.Combine(root, "connections.json");
            File.WriteAllText(path, Root(Connection()));
            Assert.Single(CompletionConnectionConfigLoader.LoadFile(path)
                .Connections);

            File.WriteAllBytes(path, new byte[
                CompletionConnectionConfigLoader.MaximumInputUtf8Bytes + 1
            ]);
            Assert.Throws<InvalidDataException>(() =>
                CompletionConnectionConfigLoader.LoadFile(path)
            );
            Assert.Throws<FileNotFoundException>(() =>
                CompletionConnectionConfigLoader.LoadFile(
                    Path.Combine(root, "missing.json")
                )
            );
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NormalizeAndValidate_RetainsFlexibleProgrammaticSemantics() {
        string endpointEnv = EnvName("PROGRAMMATIC_ENDPOINT");
        string keyEnv = EnvName("PROGRAMMATIC_KEY");
        try {
            Environment.SetEnvironmentVariable(endpointEnv, "env-endpoint");
            Environment.SetEnvironmentVariable(keyEnv, "env-key");
            CompletionConnectionsFileConfig normalized =
                CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                    [new CompletionConnectionConfig(
                        "main",
                        "custom",
                        "model",
                        string.Empty,
                        "inline-endpoint",
                        "inline-key",
                        endpointEnv,
                        keyEnv
                    )]
                ));

            CompletionConnectionConfig item = Assert.Single(
                normalized.Connections
            );
            Assert.Equal("main", normalized.DefaultConnectionId);
            Assert.Equal("custom", item.CompletionSurfaceId);
            Assert.Equal("env-endpoint", item.BaseAddress);
            Assert.Equal("env-key", item.ApiKey);
            Assert.Null(normalized.SelectableConnectionIds);
            Assert.Null(normalized.Bindings);
        }
        finally {
            Environment.SetEnvironmentVariable(endpointEnv, null);
            Environment.SetEnvironmentVariable(keyEnv, null);
        }
    }

    [Fact]
    public void NormalizeAndValidate_ValidatesAndFreezesOptionalMetadata() {
        var selectable = new List<string> { "helper", "main" };
        var bindings = new Dictionary<string, string?>(StringComparer.Ordinal) {
            ["feature.primary"] = "helper",
            ["feature.disabled"] = null,
        };
        CompletionConnectionsFileConfig normalized =
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [
                    ProgrammaticConnection("main"),
                    ProgrammaticConnection("helper"),
                ],
                DefaultConnectionId: "main",
                SelectableConnectionIds: selectable,
                Bindings: bindings
            ));

        selectable[0] = "main";
        bindings["feature.primary"] = "main";
        Assert.Equal(
            ["helper", "main"],
            normalized.SelectableConnectionIds
        );
        Assert.Equal(
            "helper",
            normalized.Bindings!["feature.primary"]
        );
        Assert.Null(normalized.Bindings["feature.disabled"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)normalized.SelectableConnectionIds!)[0] = "main"
        );
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string?>)normalized.Bindings)
                ["feature.other"] = "main"
        );
    }

    [Fact]
    public void NormalizeAndValidate_RejectsInvalidOptionalMetadata() {
        CompletionConnectionConfig main = ProgrammaticConnection("main");

        foreach (int invalidMaxTokens in new[] { 0, -1 }) {
            Assert.Throws<InvalidOperationException>(() =>
                CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                    [main with { MaxTokens = invalidMaxTokens }],
                    "main"
                ))
            );
        }
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                SelectableConnectionIds: []
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                SelectableConnectionIds: ["main", "main"]
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                SelectableConnectionIds: ["missing", "main"]
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main, ProgrammaticConnection("helper")],
                "main",
                SelectableConnectionIds: ["helper"]
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                Bindings: new Dictionary<string, string?> {
                    ["feature"] = " ",
                }
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                Bindings: new Dictionary<string, string?> {
                    ["feature"] = "missing",
                }
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                Bindings: new Dictionary<string, string?> {
                    [""] = "main",
                }
            ))
        );
        string oversizedIdentifier = new string('界', 43);
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                SelectableConnectionIds: [
                    "main",
                    oversizedIdentifier,
                ]
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                Bindings: new Dictionary<string, string?> {
                    [oversizedIdentifier] = null,
                }
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                SelectableConnectionIds: Enumerable.Repeat(
                    "main",
                    257
                ).ToArray()
            ))
        );
        Assert.Throws<InvalidOperationException>(() =>
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                [main],
                "main",
                Bindings: Enumerable.Range(0, 257).ToDictionary(
                    index => $"feature.{index}",
                    static _ => (string?)null,
                    StringComparer.Ordinal
                )
            ))
        );
    }

    [Fact]
    public void V1EnvMigration_PreservesNormalizedNonSecretFingerprint() {
        string endpointEnv = EnvName("MIGRATION_ENDPOINT");
        string keyEnv = EnvName("MIGRATION_KEY");
        try {
            Environment.SetEnvironmentVariable(
                endpointEnv,
                "https://migration.example.invalid/v1"
            );
            Environment.SetEnvironmentVariable(
                keyEnv,
                "synthetic-secret-not-for-output"
            );
            CompletionConnectionsFileConfig oldEquivalent =
                CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                    [new CompletionConnectionConfig(
                        Id: "main",
                        Kind: "openai-responses",
                        ModelId: "model",
                        CompletionSurfaceId: "openai-responses",
                        BaseAddress: string.Empty,
                        BaseAddressEnv: endpointEnv,
                        ApiKeyEnv: keyEnv,
                        MaxTokens: 4_096,
                        ReasoningEffort: CompletionReasoningEffort.High
                    )],
                    DefaultConnectionId: "main"
                ));
            CompletionConnectionsFileConfig migrated = Decode(Connection(
                kind: "openai-responses",
                surface: "openai-responses",
                source: $"\"baseAddressEnv\":\"{endpointEnv}\"",
                extra: $"\"apiKeyEnv\":\"{keyEnv}\","
                    + "\"maxTokens\":4096,\"reasoningEffort\":\"high\""
            ));

            CompletionConnectionConfig oldConnection = Assert.Single(
                oldEquivalent.Connections
            );
            CompletionConnectionConfig migratedConnection = Assert.Single(
                migrated.Connections
            );
            Assert.Equal(
                oldEquivalent.DefaultConnectionId,
                migrated.DefaultConnectionId
            );
            Assert.Equal(oldConnection.Id, migratedConnection.Id);
            Assert.Equal(oldConnection.Kind, migratedConnection.Kind);
            Assert.Equal(oldConnection.ModelId, migratedConnection.ModelId);
            Assert.Equal(
                oldConnection.CompletionSurfaceId,
                migratedConnection.CompletionSurfaceId
            );
            Assert.Equal(
                oldConnection.BaseAddress,
                migratedConnection.BaseAddress
            );
            Assert.Equal(
                oldConnection.BaseAddressEnv,
                migratedConnection.BaseAddressEnv
            );
            Assert.Equal(oldConnection.ApiKeyEnv, migratedConnection.ApiKeyEnv);
            Assert.Equal(oldConnection.MaxTokens, migratedConnection.MaxTokens);
            Assert.Equal(
                oldConnection.ReasoningEffort,
                migratedConnection.ReasoningEffort
            );
            Assert.Equal(
                oldConnection.AnthropicPromptCacheTtl,
                migratedConnection.AnthropicPromptCacheTtl
            );
            Assert.Equal(
                CompletionDispatchIdentityFactory
                    .ComputeConnectionFingerprint(oldConnection),
                CompletionDispatchIdentityFactory
                    .ComputeConnectionFingerprint(migratedConnection)
            );
        }
        finally {
            Environment.SetEnvironmentVariable(endpointEnv, null);
            Environment.SetEnvironmentVariable(keyEnv, null);
        }
    }

    private static CompletionConnectionsFileConfig Decode(
        string item,
        string defaultId = "main"
    ) => DecodeDocument(Root(item, defaultId: defaultId));

    private static CompletionConnectionsFileConfig DecodeDocument(
        string document
    ) => CompletionConnectionConfigLoader.Decode(
        Encoding.UTF8.GetBytes(document)
    );

    private static string Root(
        string item,
        string version = "1",
        string defaultId = "main",
        string extra = ""
    ) => $"{{\"v\":{version},\"connections\":[{item}],"
        + $"\"defaultConnectionId\":\"{defaultId}\""
        + (extra.Length == 0 ? string.Empty : "," + extra)
        + "}";

    private static string Connection(
        string id = "main",
        string kind = "test",
        string surface = "test-v1",
        string source = "\"baseAddress\":\"endpoint\"",
        string extra = ""
    ) {
        string suffix = extra.Length == 0 ? string.Empty : "," + extra;
        return $"{{\"id\":\"{id}\",\"kind\":\"{kind}\","
            + $"\"modelId\":\"model\",\"completionSurfaceId\":\"{surface}\","
            + source + suffix + "}";
    }

    private static CompletionConnectionsFileConfig DecodeMany(int count) {
        string[] items = Enumerable.Range(0, count)
            .Select(index => Connection(id: $"item-{index}"))
            .ToArray();
        string defaultId = count == 0 ? "none" : "item-0";
        string document = "{\"v\":1,\"connections\":["
            + string.Join(',', items)
            + $"],\"defaultConnectionId\":\"{defaultId}\"}}";
        return CompletionConnectionConfigLoader.Decode(
            Encoding.UTF8.GetBytes(document)
        );
    }

    private static CompletionConnectionConfig ProgrammaticConnection(
        string id
    ) => new(
        id,
        "custom",
        $"model-{id}",
        "custom-v1",
        "endpoint"
    );

    private static string EnvName(string suffix)
        => $"ATELIA_COMPLETION_V1_{suffix}_{Guid.NewGuid():N}";

    private static string CreateTempDirectory() {
        string path = Path.Combine(
            Path.GetTempPath(),
            "atelia-completion-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }
}
