using System.Reflection;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Gemini.Tests;

public sealed class GeminiStreamParserTests {
    private static readonly CompletionDescriptor DummyInvocation = new(
        "generativelanguage.googleapis.com",
        "google-gemini-generate-content-v1beta",
        "gemini-3-flash-preview"
    );

    [Fact]
    public void ParseEvent_AggregatesMultiFrameTextAndCapturesReplayPayloadWithThoughtSignature() {
        var result = ParseGeminiActionMessage(
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

        Assert.Equal("1\n2\n3\n4\n5", result.GetFlattenedText());

        Assert.Collection(
            result.Blocks,
            block => Assert.Equal("1\n2\n3\n4\n5", Assert.IsType<ActionBlock.Text>(block).Content),
            block => {
                using var payload = ParseReplayPayload(block);

                Assert.Equal("model", payload.RootElement.GetProperty("role").GetString());
                var parts = payload.RootElement.GetProperty("parts").EnumerateArray().ToArray();
                Assert.Equal(3, parts.Length);
                Assert.Equal("1\n2", parts[0].GetProperty("text").GetString());
                Assert.Equal("\n3\n4\n5", parts[1].GetProperty("text").GetString());
                Assert.Equal(string.Empty, parts[2].GetProperty("text").GetString());
                Assert.Equal("sig-text-123", parts[2].GetProperty("thoughtSignature").GetString());
            }
        );
    }

    [Fact]
    public void ParseEvent_ConvertsFunctionCallToToolCallAndRetainsThoughtSignatureInReplayPayload() {
        var result = ParseGeminiActionMessage(
            """
            {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"get_weather","args":{"city":"Paris","days":1},"id":"call-weather-1"},"thoughtSignature":"sig-call-123"}]}}]}
            """,
            """
            {"candidates":[{"finishReason":"STOP"}]}
            """
        );

        Assert.Collection(
            result.Blocks,
            block => {
                var toolCall = Assert.IsType<ActionBlock.ToolCall>(block).Call;
                Assert.Equal("call-weather-1", toolCall.ToolCallId);
                Assert.Equal("get_weather", toolCall.ToolName);
                AssertJsonSemanticallyEqual("""{"city":"Paris","days":1}""", toolCall.RawArgumentsJson);
            },
            block => {
                using var payload = ParseReplayPayload(block);

                Assert.Equal("model", payload.RootElement.GetProperty("role").GetString());
                var part = Assert.Single(payload.RootElement.GetProperty("parts").EnumerateArray().ToArray());
                Assert.Equal("sig-call-123", part.GetProperty("thoughtSignature").GetString());

                var functionCall = part.GetProperty("functionCall");
                Assert.Equal("get_weather", functionCall.GetProperty("name").GetString());
                Assert.Equal("call-weather-1", functionCall.GetProperty("id").GetString());
                Assert.Equal("Paris", functionCall.GetProperty("args").GetProperty("city").GetString());
                Assert.Equal(1, functionCall.GetProperty("args").GetProperty("days").GetInt32());
            }
        );
    }

    private static ActionMessage ParseGeminiActionMessage(params string[] events) {
        var parser = CreateGeminiStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);
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

    private static object CreateGeminiStreamParser() {
        var parserType = RequireGeminiType("Atelia.Completion.Gemini.GeminiStreamParser");
        return Activator.CreateInstance(parserType)
            ?? throw new InvalidOperationException($"Failed to instantiate '{parserType.FullName}'.");
    }

    private static JsonDocument ParseReplayPayload(ActionBlock block) {
        Assert.Equal(ActionBlockKind.Thinking, block.Kind);

        var payloadProperty = block.GetType().GetProperty(
            "OpaquePayload",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        Assert.True(
            payloadProperty is not null,
            $"Gemini replay block '{block.GetType().FullName}' must expose an OpaquePayload property."
        );

        var payloadValue = payloadProperty!.GetValue(block);
        var bytes = payloadValue switch {
            byte[] array => array,
            ReadOnlyMemory<byte> memory => memory.ToArray(),
            Memory<byte> memory => memory.ToArray(),
            _ => throw new InvalidOperationException(
                $"Unsupported OpaquePayload runtime type '{payloadValue?.GetType().FullName ?? "<null>"}'."
            )
        };

        return JsonDocument.Parse(bytes);
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

    private static void AssertJsonSemanticallyEqual(string expectedJson, string actualJson) {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        Assert.True(JsonElementDeepEquals(expected.RootElement, actual.RootElement));
    }

    private static bool JsonElementDeepEquals(JsonElement left, JsonElement right) {
        if (left.ValueKind != right.ValueKind) {
            return false;
        }

        switch (left.ValueKind) {
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal).ToArray();
                var rightProperties = right.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal).ToArray();
                if (leftProperties.Length != rightProperties.Length) {
                    return false;
                }

                for (var i = 0; i < leftProperties.Length; i++) {
                    if (!string.Equals(leftProperties[i].Name, rightProperties[i].Name, StringComparison.Ordinal)) {
                        return false;
                    }

                    if (!JsonElementDeepEquals(leftProperties[i].Value, rightProperties[i].Value)) {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();
                if (leftItems.Length != rightItems.Length) {
                    return false;
                }

                for (var i = 0; i < leftItems.Length; i++) {
                    if (!JsonElementDeepEquals(leftItems[i], rightItems[i])) {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Number:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

            case JsonValueKind.True:
            case JsonValueKind.False:
                return left.GetBoolean() == right.GetBoolean();

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;

            default:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
        }
    }
}
