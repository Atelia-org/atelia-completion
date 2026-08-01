using Xunit;

namespace Atelia.Completion.Tests;

public sealed class CompletionConnectionConfigLoaderTests {
    [Fact]
    public void LoadFile_AllowsBaseAddressAndApiKeyFromEnvironment() {
        string baseAddressEnv = CreateEnvName(nameof(LoadFile_AllowsBaseAddressAndApiKeyFromEnvironment), "BASE");
        string apiKeyEnv = CreateEnvName(nameof(LoadFile_AllowsBaseAddressAndApiKeyFromEnvironment), "KEY");
        string tempDirectory = CreateTempDirectory();

        try {
            Environment.SetEnvironmentVariable(baseAddressEnv, "http://localhost:8888/");
            Environment.SetEnvironmentVariable(apiKeyEnv, "sk-test");
            string path = Path.Combine(tempDirectory, "connections.json");
            File.WriteAllText(
                path,
                $$"""
                {
                  "defaultConnectionId": "local-qwen",
                  "connections": [
                    {
                      "id": "local-qwen",
                      "kind": "openai-chat",
                      "modelId": "unsloth/qwen3.6",
                      "completionSurfaceId": "openai-chat/sglang-compatible",
                      "requestTimeoutSeconds": 300,
                      "baseAddressEnv": "{{baseAddressEnv}}",
                      "apiKeyEnv": "{{apiKeyEnv}}"
                    }
                  ]
                }
                """
            );

            var config = CompletionConnectionConfigLoader.LoadFile(path);

            var connection = Assert.Single(config.Connections);
            Assert.Equal("local-qwen", config.DefaultConnectionId);
            Assert.Equal("http://localhost:8888/", connection.BaseAddress);
            Assert.Equal("sk-test", connection.ApiKey);
            Assert.Equal(baseAddressEnv, connection.BaseAddressEnv);
            Assert.Equal(apiKeyEnv, connection.ApiKeyEnv);
            Assert.Equal(300, connection.RequestTimeoutSeconds);
        }
        finally {
            Environment.SetEnvironmentVariable(baseAddressEnv, null);
            Environment.SetEnvironmentVariable(apiKeyEnv, null);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void LoadFile_RejectsRequestTimeoutOutsideBoundedRange(
        int requestTimeoutSeconds
    ) {
        string tempDirectory = CreateTempDirectory();
        try {
            string path = Path.Combine(tempDirectory, "connections.json");
            File.WriteAllText(
                path,
                $$"""
                {
                  "connections": [
                    {
                      "id": "bounded-timeout",
                      "kind": "openai-chat",
                      "modelId": "model-a",
                      "completionSurfaceId": "openai-chat/strict",
                      "baseAddress": "http://localhost/",
                      "requestTimeoutSeconds": {{requestTimeoutSeconds}}
                    }
                  ]
                }
                """
            );

            InvalidOperationException exception = Assert.Throws<
                InvalidOperationException
            >(() => CompletionConnectionConfigLoader.LoadFile(path));

            Assert.Contains(
                "requestTimeoutSeconds must be between 1 and 3600",
                exception.Message,
                StringComparison.Ordinal
            );
        }
        finally {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void LoadFile_ReportsMissingBaseAddressEnvironmentVariable() {
        string baseAddressEnv = CreateEnvName(nameof(LoadFile_ReportsMissingBaseAddressEnvironmentVariable), "BASE");
        string tempDirectory = CreateTempDirectory();

        try {
            Environment.SetEnvironmentVariable(baseAddressEnv, null);
            string path = Path.Combine(tempDirectory, "connections.json");
            File.WriteAllText(
                path,
                $$"""
                {
                  "connections": [
                    {
                      "id": "local-qwen",
                      "kind": "openai-chat",
                      "modelId": "unsloth/qwen3.6",
                      "completionSurfaceId": "openai-chat/sglang-compatible",
                      "baseAddressEnv": "{{baseAddressEnv}}"
                    }
                  ]
                }
                """
            );

            var ex = Assert.Throws<InvalidOperationException>(() => CompletionConnectionConfigLoader.LoadFile(path));
            Assert.Contains($"baseAddressEnv references environment variable '{baseAddressEnv}'", ex.Message, StringComparison.Ordinal);
        }
        finally {
            Environment.SetEnvironmentVariable(baseAddressEnv, null);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateEnvName(string testName, string suffix)
        => $"ATELIA_TEST_{testName}_{suffix}_{Guid.NewGuid():N}";

    private static string CreateTempDirectory() {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "atelia-completion-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }
}
