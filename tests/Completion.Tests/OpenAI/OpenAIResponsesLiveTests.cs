using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesLiveTests {
    private const string OpenRouterApiKeyEnvVar = "OPENROUTER_API_KEY";

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_OpenRouter_ReturnsExpectedText() {
        var apiKey = Environment.GetEnvironmentVariable(OpenRouterApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey)) { return; }

        using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(new Uri("https://openrouter.ai/api/"));
        var client = new OpenAIResponsesClient(apiKey, httpClient);
        var request = new CompletionRequest(
            ModelId: "openai/gpt-5.4-mini",
            SystemPrompt: "Answer tersely.",
            Context: [new ObservationMessage("Reply with exactly OK.")],
            Tools: ImmutableArray<ToolDefinition>.Empty
        );

        var result = await client.StreamCompletionAsync(request, observer: null, CancellationToken.None);

        Assert.Equal("OK", result.Message.GetFlattenedText().Trim());
    }

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_OpenRouter_CanReturnToolCall() {
        var apiKey = Environment.GetEnvironmentVariable(OpenRouterApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey)) { return; }

        using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(new Uri("https://openrouter.ai/api/"));
        var client = new OpenAIResponsesClient(apiKey, httpClient);
        var request = new CompletionRequest(
            ModelId: "openai/gpt-5.4-mini",
            SystemPrompt: "You are a strict tool-using assistant.",
            Context: [new ObservationMessage("Use the get_weather tool for Paris. Do not answer without calling the tool.")],
            Tools: ImmutableArray.Create(
                new ToolDefinition(
                    "get_weather",
                    "Look up weather.",
                    new ToolSchema.Object(
                        properties: [
                            new ToolSchema.Property(
                                "city",
                                new ToolSchema.Value(ToolParamType.String, description: "City name."),
                                isRequired: true
                            )
                        ]
                    )
                )
            )
        );

        var result = await client.StreamCompletionAsync(request, observer: null, CancellationToken.None);

        var toolCall = Assert.IsType<ActionBlock.ToolCall>(Assert.Single(result.Message.Blocks));
        Assert.Equal("get_weather", toolCall.Call.ToolName);
        Assert.Contains("Paris", toolCall.Call.RawArgumentsJson, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(toolCall.Call.ToolCallId));
    }
}
