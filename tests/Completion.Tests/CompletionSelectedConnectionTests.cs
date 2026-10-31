using System.Text;
using System.Text.Json;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class CompletionSelectedConnectionTests {
    [Fact]
    public void ExactSelectionDoesNotResolveUnusedEnvironmentWhileWholeCatalogRetainsItsContract() {
        string missingEndpoint = "ATELIA_UNUSED_ENDPOINT_" + Guid.NewGuid().ToString("N");
        string missingSecret = "ATELIA_UNUSED_SECRET_" + Guid.NewGuid().ToString("N");
        byte[] catalog = Catalog(Connection("selected"), new {
            id = "unused", kind = "openai-responses", modelId = "unused-model", completionSurfaceId = "openai-responses",
            baseAddressEnv = missingEndpoint, apiKeyEnv = missingSecret
        });
        Assert.Null(Environment.GetEnvironmentVariable(missingEndpoint));
        Assert.Null(Environment.GetEnvironmentVariable(missingSecret));

        CompletionConnectionConfig selected = CompletionConnectionConfigLoader.DecodeSelectedConnection(catalog, "selected");
        Assert.Equal("selected", selected.Id);
        Assert.Equal("https://fixture.invalid/", selected.BaseAddress);
        Assert.Null(selected.ApiKey);
        Assert.Throws<InvalidOperationException>(() => CompletionConnectionConfigLoader.DecodeCatalog(catalog));
    }

    [Fact]
    public void SelectedEnvironmentIsResolvedNormally() {
        string endpoint = "ATELIA_SELECTED_ENDPOINT_" + Guid.NewGuid().ToString("N");
        string secret = "ATELIA_SELECTED_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(endpoint, "https://selected.invalid/");
        Environment.SetEnvironmentVariable(secret, "fixture-not-a-real-key");
        try {
            byte[] catalog = Catalog(new {
                id = "selected", kind = "openai-responses", modelId = "selected-model", completionSurfaceId = "openai-responses",
                baseAddressEnv = endpoint, apiKeyEnv = secret
            });
            CompletionConnectionConfig selected = CompletionConnectionConfigLoader.DecodeSelectedConnection(catalog, "selected");
            Assert.Equal("https://selected.invalid/", selected.BaseAddress);
            Assert.Equal("fixture-not-a-real-key", selected.ApiKey);
            Assert.Equal("selected-model", selected.ModelId);
        } finally {
            Environment.SetEnvironmentVariable(endpoint, null);
            Environment.SetEnvironmentVariable(secret, null);
        }
    }

    [Fact]
    public void MissingSelectedEnvironmentStillFails() {
        byte[] catalog = Catalog(new {
            id = "selected", kind = "openai-responses", modelId = "model", completionSurfaceId = "openai-responses",
            baseAddressEnv = "ATELIA_MISSING_SELECTED_" + Guid.NewGuid().ToString("N")
        });
        Assert.Throws<InvalidOperationException>(() => CompletionConnectionConfigLoader.DecodeSelectedConnection(catalog, "selected"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("SELECTED")]
    public void SelectionIsExactAndNeverFallsBack(string requested) {
        Assert.Throws<ArgumentException>(() => CompletionConnectionConfigLoader.DecodeSelectedConnection(Catalog(Connection("selected")), requested));
    }

    [Theory]
    [InlineData("{\"v\":3,\"connections\":[SELECTED],\"unknown\":true}")]
    [InlineData("{\"v\":3,\"connections\":[SELECTED],\"defaultConnectionId\":\"selected\"}")]
    [InlineData("{\"v\":3,\"connections\":[SELECTED,SELECTED]}")]
    [InlineData("{\"v\":3,\"connections\":[SELECTED,{\"id\":\"broken-unused\"}]}")]
    [InlineData("{\"v\":3,\"connections\":[SELECTED],\"bindings\":{\"role\":\"missing\"}}")]
    [InlineData("{\"v\":3,\"connections\":[SELECTED],\"selectableConnectionIds\":[\"missing\"]}")]
    [InlineData("{\"v\":3,\"connections\":[SELECTED],\"selectableConnectionIds\":[\"selected\",\"selected\"]}")]
    [InlineData("{\"v\":3.0,\"connections\":[SELECTED]}")]
    [InlineData("{\"v\":3,\"connections\":[SELECTED,]}")]
    [InlineData("{\"v\":3,\"connections\":[SELECTED]} trailing")]
    public void SelectionDoesNotBypassStrictWholeDocumentValidation(string template) {
        byte[] catalog = Encoding.UTF8.GetBytes(template.Replace("SELECTED", JsonSerializer.Serialize(Connection("selected")), StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => CompletionConnectionConfigLoader.DecodeSelectedConnection(catalog, "selected"));
    }

    [Fact]
    public void UnselectedEntriesStillRejectConflictingCredentialSourcesAndUnknownFields() {
        foreach (object unused in new object[] {
            new { id = "unused", kind = "openai-responses", modelId = "model", completionSurfaceId = "openai-responses",
                baseAddress = "https://unused.invalid/", baseAddressEnv = "IGNORED" },
            new { id = "unused", kind = "openai-responses", modelId = "model", completionSurfaceId = "openai-responses",
                baseAddress = "https://unused.invalid/", apiKey = "fixture", apiKeyEnv = "IGNORED" },
            new { id = "unused", kind = "openai-responses", modelId = "model", completionSurfaceId = "openai-responses",
                baseAddress = "https://unused.invalid/", unexpected = true }
        }) {
            Assert.Throws<InvalidDataException>(() => CompletionConnectionConfigLoader.DecodeSelectedConnection(Catalog(Connection("selected"), unused), "selected"));
        }
    }

    [Fact]
    public void SelectedFileLoadingUsesBoundedStrictUtf8AndLeavesFileUnchanged() {
        string path = Path.Combine(Path.GetTempPath(), "atelia-selected-" + Guid.NewGuid().ToString("N") + ".json");
        byte[] catalog = Catalog(Connection("selected"));
        try {
            File.WriteAllBytes(path, catalog);
            Assert.Equal("selected", CompletionConnectionConfigLoader.LoadSelectedConnectionFile(path, "selected").Id);
            Assert.Equal(catalog, File.ReadAllBytes(path));
            File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, .. catalog]);
            Assert.Throws<InvalidDataException>(() => CompletionConnectionConfigLoader.LoadSelectedConnectionFile(path, "selected"));
            File.WriteAllBytes(path, new byte[CompletionConnectionConfigLoader.MaximumInputUtf8Bytes + 1]);
            Assert.Throws<InvalidDataException>(() => CompletionConnectionConfigLoader.LoadSelectedConnectionFile(path, "selected"));
        } finally { File.Delete(path); }
    }

    private static byte[] Catalog(params object[] connections) => JsonSerializer.SerializeToUtf8Bytes(new { v = 3, connections });
    private static object Connection(string id) => new {
        id, kind = "openai-responses", modelId = "model", completionSurfaceId = "openai-responses", baseAddress = "https://fixture.invalid/"
    };
}
