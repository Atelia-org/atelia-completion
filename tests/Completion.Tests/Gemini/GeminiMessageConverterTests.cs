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
        if (!GeminiProductionTypesPresent()) {
            return;
        }

        var request = new CompletionRequest(
            ModelId: "gemini-2.5-flash",
            SystemPrompt: "Follow the system policy.",
            Context: new IHistoryMessage[] { new ObservationMessage("hello") },
            Tools: ImmutableArray<ToolDefinition>.Empty
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
        if (!GeminiProductionTypesPresent()) {
            return;
        }

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
            Content: "Observed external state.",
            Results: new[] {
                new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
            },
            ExecuteError: "runner_failed"
        );

        var request = new CompletionRequest(
            ModelId: "gemini-2.5-flash",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] { actionMessage, toolResults },
            Tools: ImmutableArray<ToolDefinition>.Empty
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

        var userContentJson = contents[1].GetRawText();
        Assert.Contains("Observed external state.", userContentJson, StringComparison.Ordinal);
        Assert.Contains("runner_failed", userContentJson, StringComparison.Ordinal);
        Assert.Contains("ok", userContentJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToApiRequest_ToolReplayWithoutGeminiReplayPayloadFailsFast() {
        if (!GeminiProductionTypesPresent()) {
            return;
        }

        var request = new CompletionRequest(
            ModelId: "gemini-2.5-flash",
            SystemPrompt: string.Empty,
            Context: new IHistoryMessage[] {
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
                    Content: null,
                    Results: new[] {
                        new ToolResult("search", "call-1", ToolExecutionStatus.Success, "ok")
                    },
                    ExecuteError: null
                )
            },
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var exception = Assert.Throws<InvalidOperationException>(
            () => ConvertToApiRequest(request)
        );

        Assert.Contains("Gemini", exception.Message, StringComparison.Ordinal);
        Assert.Contains("replay", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    ) {
        var replayBlockType = typeof(CompletionHttpTransportFactory).Assembly.GetType("Atelia.Completion.Gemini.GeminiReplayBlock");
        Assert.NotNull(replayBlockType);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new JsonObject {
                ["role"] = "model",
                ["parts"] = new JsonArray {
                    new JsonObject {
                        ["thoughtSignature"] = thoughtSignature,
                        ["functionCall"] = new JsonObject {
                            ["name"] = toolName,
                            ["id"] = toolCallId,
                            ["args"] = JsonNode.Parse(rawArgumentsJson)
                        }
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
