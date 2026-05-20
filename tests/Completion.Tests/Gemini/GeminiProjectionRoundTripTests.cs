using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Gemini.Tests;

public sealed class GeminiProjectionRoundTripTests {
    private static readonly CompletionDescriptor GeminiInvocation = new(
        "generativelanguage.googleapis.com",
        "google-gemini-generate-content-v1beta",
        "gemini-3-flash-preview"
    );

    private static readonly ImmutableArray<ToolDefinition> WeatherTools = [
        ToolDefinition.CreateFlat(
            name: "get_weather",
            description: "Get weather by city.",
            parameters: [
                new ToolParamSpec("city", "The city name.", ToolParamType.String),
                new ToolParamSpec("days", "Forecast days.", ToolParamType.Int32)
            ]
        )
    ];

    [Fact]
    public void TextTurnWithThoughtSignature_RoundTripsProviderNativeReplayParts() {
        var parsed = ParseGeminiActionMessage(
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"1\n2"}]}}]}
            """,
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"\n3\n4\n5"}]}}]}
            """,
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"","thoughtSignature":"sig-text-123"}]},"finishReason":"STOP"}]}
            """
        );

        using var apiRequest = ConvertToGeminiApiRequest(
            new CompletionRequest(
                ModelId: "gemini-3-flash-preview",
                SystemPrompt: string.Empty,
                Context: [parsed],
                Tools: WeatherTools
            )
        );

        var contents = apiRequest.RootElement.GetProperty("contents");
        var modelContent = Assert.Single(contents.EnumerateArray().ToArray());
        Assert.Equal("model", modelContent.GetProperty("role").GetString());

        var parts = modelContent.GetProperty("parts").EnumerateArray().ToArray();
        Assert.Equal(3, parts.Length);
        Assert.Equal("1\n2", parts[0].GetProperty("text").GetString());
        Assert.Equal("\n3\n4\n5", parts[1].GetProperty("text").GetString());
        Assert.Equal(string.Empty, parts[2].GetProperty("text").GetString());
        Assert.Equal("sig-text-123", parts[2].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public void FunctionCallWithThoughtSignature_RoundTripsToModelReplayContentBeforeFunctionResponse() {
        var parsed = ParseGeminiActionMessage(
            """
            {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"get_weather","args":{"city":"Paris","days":1},"id":"call-weather-1"},"thoughtSignature":"sig-call-123"}]}}]}
            """,
            """
            {"candidates":[{"finishReason":"STOP"}]}
            """
        );

        using var apiRequest = ConvertToGeminiApiRequest(
            new CompletionRequest(
                ModelId: "gemini-3-flash-preview",
                SystemPrompt: string.Empty,
                Context: [
                    new ObservationMessage("What's the weather in Paris tomorrow?"),
                    parsed,
                    new ToolResultsMessage(
                        Content: null,
                        Results: [
                            new ToolResult("get_weather", "call-weather-1", ToolExecutionStatus.Success, """{"tempC":18}""")
                        ],
                        ExecuteError: null
                    )
                ],
                Tools: WeatherTools
            )
        );

        Assert.Collection(
            apiRequest.RootElement.GetProperty("contents").EnumerateArray().ToArray(),
            userContent => {
                Assert.Equal("user", userContent.GetProperty("role").GetString());
                var textPart = Assert.Single(userContent.GetProperty("parts").EnumerateArray().ToArray());
                Assert.Equal("What's the weather in Paris tomorrow?", textPart.GetProperty("text").GetString());
            },
            modelContent => {
                Assert.Equal("model", modelContent.GetProperty("role").GetString());
                var part = Assert.Single(modelContent.GetProperty("parts").EnumerateArray().ToArray());
                Assert.Equal("sig-call-123", part.GetProperty("thoughtSignature").GetString());

                var functionCall = part.GetProperty("functionCall");
                Assert.Equal("get_weather", functionCall.GetProperty("name").GetString());
                Assert.Equal("call-weather-1", functionCall.GetProperty("id").GetString());
                Assert.Equal("Paris", functionCall.GetProperty("args").GetProperty("city").GetString());
                Assert.Equal(1, functionCall.GetProperty("args").GetProperty("days").GetInt32());
            },
            userContent => {
                Assert.Equal("user", userContent.GetProperty("role").GetString());
                var functionResponsePart = Assert.Single(userContent.GetProperty("parts").EnumerateArray().ToArray());
                Assert.True(
                    functionResponsePart.TryGetProperty("functionResponse", out _),
                    "Gemini tool continuation should project ToolResultsMessage as a user functionResponse part."
                );
            }
        );
    }

    private static ActionMessage ParseGeminiActionMessage(params string[] events) {
        var parser = CreateGeminiStreamParser();
        var aggregator = new CompletionAggregator(GeminiInvocation);
        var parserType = parser.GetType();

        var parseEvent = RequireMethod(
            parserType,
            "ParseEvent",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            typeof(string),
            typeof(CompletionAggregator)
        );

        foreach (var e in events) {
            parseEvent.Invoke(parser, [e, aggregator]);
        }

        var complete = RequireMethod(
            parserType,
            "Complete",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            typeof(CompletionAggregator)
        );
        complete.Invoke(parser, [aggregator]);

        return aggregator.Build().Message;
    }

    private static JsonDocument ConvertToGeminiApiRequest(CompletionRequest request) {
        var converterType = RequireGeminiType("Atelia.Completion.Gemini.GeminiMessageConverter");
        var convertMethod = RequireMethod(
            converterType,
            "ConvertToApiRequest",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            typeof(CompletionRequest)
        );

        var apiRequest = convertMethod.Invoke(null, [request]);
        Assert.True(
            apiRequest is not null,
            $"Blocked: '{converterType.FullName}.ConvertToApiRequest' returned null."
        );

        return JsonDocument.Parse(JsonSerializer.Serialize(apiRequest, apiRequest!.GetType()));
    }

    private static object CreateGeminiStreamParser() {
        var parserType = RequireGeminiType("Atelia.Completion.Gemini.GeminiStreamParser");
        return Activator.CreateInstance(parserType)
            ?? throw new InvalidOperationException($"Failed to instantiate '{parserType.FullName}'.");
    }

    private static Type RequireGeminiType(string fullName) {
        var assembly = typeof(CompletionAggregator).Assembly;
        var type = assembly.GetType(fullName);
        Assert.True(
            type is not null,
            $"Blocked: expected Gemini production type '{fullName}' in assembly '{assembly.GetName().Name}', but it is not present in this workspace yet."
        );
        return type!;
    }

    private static MethodInfo RequireMethod(Type type, string name, BindingFlags flags, params Type[] parameterTypes) {
        var method = type.GetMethod(name, flags, parameterTypes);
        Assert.True(
            method is not null,
            $"Blocked: expected method '{type.FullName}.{name}({string.Join(", ", parameterTypes.Select(static t => t.Name))})' was not found."
        );
        return method!;
    }
}
