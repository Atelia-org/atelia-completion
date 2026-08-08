using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.Gemini.Tests;

public sealed class GeminiMessageConverterTests {
    [Fact]
    public void ConvertToApiRequest_MapsSystemPromptToSystemInstruction() {
        if (!GeminiProductionTypesPresent()) { return; }

        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                "Follow the system policy.",
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { new ObservationMessage("hello") }
            ),
            tailMessages: []
        );

        using var document = SerializeApiRequest(ConvertToApiRequest(request));
        var root = document.RootElement;
        var systemInstruction = root.GetProperty("systemInstruction");
        var parts = systemInstruction.GetProperty("parts").EnumerateArray().ToArray();

        Assert.Single(parts);
        Assert.Equal("Follow the system policy.", parts[0].GetProperty("text").GetString());

        var contents = root.GetProperty("contents").EnumerateArray().ToArray();
        var userContent = Assert.Single(contents);
        Assert.Equal("user", userContent.GetProperty("role").GetString());
        Assert.Equal("hello", userContent.GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ConvertToApiRequest_MapsToolResultsToFunctionResponse() {
        if (!GeminiProductionTypesPresent()) { return; }

        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                CreateGeminiReplayBlock(
                    toolName: "search",
                    toolCallId: "call-1",
                    rawArgumentsJson: "{\"query\":\"weather\"}",
                    thoughtSignature: "sig-1"
                )
            }
        );

        var toolResults = new ToolResultsMessage(
            content: "Observed external state.",
            results: new[] {
                new ToolResult(
                    "search",
                    "call-1",
                    ToolExecutionStatus.Success,
                    new ToolResultBlock[] {
                        new ToolResultBlock.Text("alpha"),
                        new ToolResultBlock.Text("omega")
                    }
                )
            }
        );

        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { actionMessage, toolResults }
            ),
            tailMessages: []
        );

        using var document = SerializeApiRequest(ConvertToApiRequest(request));
        var contents = document.RootElement.GetProperty("contents").EnumerateArray().ToArray();

        Assert.Equal(2, contents.Length);
        Assert.Equal("model", contents[0].GetProperty("role").GetString());
        Assert.Equal("user", contents[1].GetProperty("role").GetString());

        var functionResponsePart = contents[1]
            .GetProperty("parts")
            .EnumerateArray()
            .Single(
            part => part.TryGetProperty("functionResponse", out var functionResponseElement)
                    && functionResponseElement.ValueKind is not JsonValueKind.Null
        );
        var functionResponse = functionResponsePart.GetProperty("functionResponse");

        Assert.Equal("search", functionResponse.GetProperty("name").GetString());
        Assert.Equal("call-1", functionResponse.GetProperty("id").GetString());
        Assert.Equal(
            "alphaomega",
            functionResponse.GetProperty("response").GetProperty("result").GetString()
        );

        var textPart = contents[1]
            .GetProperty("parts")
            .EnumerateArray()
            .Single(
                part => part.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind is not JsonValueKind.Null
            );
        Assert.Equal("Observed external state.", textPart.GetProperty("text").GetString());
    }

    [Fact]
    public void ConvertToApiRequest_ToolDependencyCanCrossPrefixTailBoundary() {
        if (!GeminiProductionTypesPresent()) { return; }

        ActionBlock replayBlock = CreateGeminiReplayBlock(
            toolName: "search",
            toolCallId: "call-1",
            rawArgumentsJson: "{}",
            thoughtSignature: "sig-1"
        );
        var results = new ToolResultsMessage(
            content: null,
            results: [
                ToolResult.FromText(
                    "search",
                    "call-1",
                    ToolExecutionStatus.Success,
                    "ok"
                )
            ]
        );
        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault([]),
                [new ActionMessage([replayBlock])]
            ),
            [results]
        );

        using JsonDocument document = SerializeApiRequest(
            ConvertToApiRequest(request)
        );

        JsonElement[] contents = document.RootElement
            .GetProperty("contents")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, contents.Length);
        Assert.Equal("model", contents[0].GetProperty("role").GetString());
        Assert.Equal("user", contents[1].GetProperty("role").GetString());
    }

    [Fact]
    public void ConvertToApiRequest_MapsRequiredNamedToolChoice() {
        if (!GeminiProductionTypesPresent()) { return; }

        var tool = new ToolDefinition(
            "emit_result",
            "Emit one result.",
            new ToolSchema.Object()
        );
        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                string.Empty,
                new CompletionOutputContract(
                    [tool],
                    CompletionToolChoice.RequiredNamed("emit_result")
                ),
                [new ObservationMessage("emit")]
            ),
            tailMessages: []
        );

        using JsonDocument document = SerializeApiRequest(
            ConvertToApiRequest(request)
        );
        JsonElement config = document.RootElement
            .GetProperty("toolConfig")
            .GetProperty("functionCallingConfig");

        Assert.Equal("ANY", config.GetProperty("mode").GetString());
        Assert.Equal(
            "emit_result",
            Assert.Single(
                config.GetProperty("allowedFunctionNames").EnumerateArray()
            ).GetString()
        );
    }

    [Fact]
    public void ConvertToApiRequest_RejectsDisableParallelPolicy() {
        if (!GeminiProductionTypesPresent()) { return; }

        var tool = new ToolDefinition(
            "emit_result",
            "Emit one result.",
            new ToolSchema.Object()
        );
        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                string.Empty,
                new CompletionOutputContract(
                    [tool],
                    CompletionToolChoice.Auto,
                    allowParallelToolCalls: false
                ),
                [new ObservationMessage("emit")]
            ),
            tailMessages: []
        );

        Assert.Throws<NotSupportedException>(
            () => ConvertToApiRequest(request)
        );
    }

    [Fact]
    public void ConvertToApiRequest_ToolReplayWithoutGeminiReplayPayloadFailsFast() {
        if (!GeminiProductionTypesPresent()) { return; }

        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    new ActionBlock[] {
                        new ActionBlock.ToolCall(
                            new RawToolCall(
                                ToolName: "search",
                                ToolCallId: "call-1",
                                RawArgumentsJson: "{\"query\":\"weather\"}"
                            )
                        )
                    }
                ),
                new ToolResultsMessage(
                    content: null,
                    results: new[] {
                        ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "ok")
                    }
                )
            }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => ConvertToApiRequest(request)
        );

        Assert.Contains("Gemini", exception.Message, StringComparison.Ordinal);
        Assert.Contains("replay", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConvertToApiRequest_MissingPendingToolResultsThrows() {
        if (!GeminiProductionTypesPresent()) { return; }

        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                CreateGeminiReplayBlock(
                    ("search", "call-1", "{\"query\":\"weather\"}", "sig-1"),
                    ("lookup", "call-2", "{\"id\":42}", "sig-2")
                )
            }
        );

        var toolResults = new ToolResultsMessage(
            content: null,
            results: new[] {
                ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "ok")
            }
        );

        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { actionMessage, toolResults }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => ConvertToApiRequest(request)
        );

        Assert.Contains("call-2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("align 1:1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_ToolNameMismatchThrows() {
        if (!GeminiProductionTypesPresent()) { return; }

        var actionMessage = new ActionMessage(
            new ActionBlock[] {
                CreateGeminiReplayBlock(
                    toolName: "search",
                    toolCallId: "call-1",
                    rawArgumentsJson: "{\"query\":\"weather\"}",
                    thoughtSignature: "sig-1"
                )
            }
        );

        var toolResults = new ToolResultsMessage(
            content: null,
            results: new[] {
                ToolResult.FromText("lookup", "call-1", ToolExecutionStatus.Success, "ok")
            }
        );

        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] { actionMessage, toolResults }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => ConvertToApiRequest(request)
        );

        Assert.Contains("expected 'search'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("got 'lookup'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ToolCallId + ToolName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_ReplayPayloadThatDriftsFromVisibleTextFailsFast() {
        if (!GeminiProductionTypesPresent()) { return; }

        var replayBlockType = typeof(CompletionHttpTransportFactory).Assembly.GetType("Atelia.Completion.Gemini.GeminiReplayBlock");
        Assert.NotNull(replayBlockType);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new JsonObject {
                ["role"] = "model",
                ["parts"] = new JsonArray {
                    new JsonObject {
                        ["text"] = "provider text",
                        ["thoughtSignature"] = "sig-text"
                    }
                }
            }
        );

        var constructor = replayBlockType!.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(ReadOnlyMemory<byte>), typeof(CompletionDescriptor), typeof(string) },
            modifiers: null
        );
        Assert.NotNull(constructor);

        var replayBlock = Assert.IsAssignableFrom<ActionBlock>(
            constructor!.Invoke(
                new object?[] {
                    new ReadOnlyMemory<byte>(payload),
                    new CompletionDescriptor(
                        "generativelanguage.googleapis.com",
                        "google-gemini-generate-content-v1beta",
                        "gemini-2.5-flash"
                    ),
                    null
                }
            )
        );

        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    new ActionBlock[] {
                        new ActionBlock.Text("visible text"),
                        replayBlock
                    }
                )
            }
            ),
            tailMessages: []
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => ConvertToApiRequest(request)
        );

        Assert.Contains("two sources of truth", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static object ConvertToApiRequest(CompletionRequest request) {
        var converterType = typeof(CompletionHttpTransportFactory).Assembly.GetType("Atelia.Completion.Gemini.GeminiMessageConverter");
        Assert.NotNull(converterType);
        var method = converterType.GetMethod(
            "ConvertToApiRequest",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(CompletionRequest) },
            modifiers: null
        );

        Assert.NotNull(method);

        try {
            return method!.Invoke(null, new object?[] { request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static JsonDocument SerializeApiRequest(object apiRequest) {
        var json = JsonSerializer.Serialize(apiRequest, apiRequest.GetType());
        return JsonDocument.Parse(json);
    }

    private static ActionBlock CreateGeminiReplayBlock(
        string toolName,
        string toolCallId,
        string rawArgumentsJson,
        string thoughtSignature
    ) => CreateGeminiReplayBlock((toolName, toolCallId, rawArgumentsJson, thoughtSignature));

    private static ActionBlock CreateGeminiReplayBlock(
        params (string ToolName, string ToolCallId, string RawArgumentsJson, string ThoughtSignature)[] functionCalls
    ) {
        var replayBlockType = typeof(CompletionHttpTransportFactory).Assembly.GetType("Atelia.Completion.Gemini.GeminiReplayBlock");
        Assert.NotNull(replayBlockType);

        var parts = new JsonArray();
        foreach (var functionCall in functionCalls) {
            parts.Add(
                new JsonObject {
                    ["thoughtSignature"] = functionCall.ThoughtSignature,
                    ["functionCall"] = new JsonObject {
                        ["name"] = functionCall.ToolName,
                        ["id"] = functionCall.ToolCallId,
                        ["args"] = JsonNode.Parse(functionCall.RawArgumentsJson)
                    }
                }
            );
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new JsonObject {
                ["role"] = "model",
                ["parts"] = parts
            }
        );

        var constructor = replayBlockType!.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(ReadOnlyMemory<byte>), typeof(CompletionDescriptor), typeof(string) },
            modifiers: null
        );

        Assert.NotNull(constructor);

        var invocation = new CompletionDescriptor(
            "generativelanguage.googleapis.com",
            "google-gemini-generate-content-v1beta",
            "gemini-2.5-flash"
        );

        return Assert.IsAssignableFrom<ActionBlock>(
            constructor!.Invoke(new object?[] { new ReadOnlyMemory<byte>(payload), invocation, null })
        );
    }

    private static bool GeminiProductionTypesPresent() {
        var assembly = typeof(CompletionHttpTransportFactory).Assembly;
        return assembly.GetType("Atelia.Completion.Gemini.GeminiMessageConverter") is not null;
    }
}
