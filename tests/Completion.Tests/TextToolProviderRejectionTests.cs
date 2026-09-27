using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.Gemini;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class TextToolProviderRejectionTests {
    public static TheoryData<string, string, bool> UnsupportedInputs {
        get {
            var data = new TheoryData<string, string, bool>();
            foreach (string provider in new[] { "chat", "deepseek", "anthropic", "gemini" }) {
                foreach (string input in new[] { "definition", "prefix-history", "tail-history" }) {
                    data.Add(provider, input, false);
                    data.Add(provider, input, true);
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnsupportedInputs))]
    public async Task TextInput_IsRejectedBeforeAnyHttpIncludingMetadata(
        string provider,
        string input,
        bool explicitInvocationOptions
    ) {
        using var handler = new NoDispatchHandler();
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("https://provider.invalid/")
        };
        ICompletionClient client = provider switch {
            "chat" => new OpenAIChatClient(null, httpClient),
            "deepseek" => new DeepSeekV4ChatClient(null, httpClient),
            "anthropic" => new AnthropicClient(null, httpClient),
            "gemini" => new GeminiClient(null, httpClient),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        const string sensitiveMarker = "private-input-marker";
        var textCall = new ActionMessage([
            new ActionBlock.ToolCall(RawToolCall.FromText(sensitiveMarker, "call-1", sensitiveMarker))
        ]);
        var request = new CompletionRequest(
            "unknown-model-requires-metadata",
            new CompletionPromptPrefix(
                "system",
                CompletionOutputContract.ProviderDefault(input == "definition"
                    ? [ToolDefinition.FromText(sensitiveMarker, sensitiveMarker)]
                    : []),
                input == "prefix-history" ? [textCall] : []
            ),
            input == "tail-history" ? [textCall] : []
        );

        CompletionRequestRejectedException rejection = await Assert.ThrowsAsync<CompletionRequestRejectedException>(
            () => explicitInvocationOptions
                ? client.StreamCompletionAsync(request, CompletionInvocationOptions.Default, observer: null)
                : client.StreamCompletionAsync(request, observer: null)
        );

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(CompletionTerminationKind.Failed, rejection.Termination.Kind);
        Assert.Equal("tool-input.unsupported-text", rejection.Termination.ProviderReason);
        Assert.DoesNotContain(sensitiveMarker, rejection.Termination.Detail ?? "", StringComparison.Ordinal);
        Assert.Equal(["adapter-validation=tool-input-kind"], rejection.Errors);
    }

    private sealed class NoDispatchHandler : HttpMessageHandler {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            RequestCount++;
            throw new InvalidOperationException("No HTTP request is allowed for unsupported tool input.");
        }
    }
}
