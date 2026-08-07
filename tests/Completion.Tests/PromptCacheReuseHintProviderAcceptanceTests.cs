using System.Net;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Gemini;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class PromptCacheReuseHintProviderAcceptanceTests {
    [Fact]
    public async Task OpenAIChat_AcceptsHintAsExplicitNoOp() {
        var handler = new RepeatingHandler(CreateOpenAIChatResponse);
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, httpClient);

        await InvokeLegacyAndHintedAsync(client);

        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
    }

    [Fact]
    public async Task OpenAIResponses_AcceptsHintAsExplicitNoOp() {
        var handler = new RepeatingHandler(CreateOpenAIResponsesResponse);
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIResponsesClient(null, httpClient);

        await InvokeLegacyAndHintedAsync(client);

        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
    }

    [Fact]
    public async Task Gemini_AcceptsHintAsExplicitNoOp() {
        var handler = new RepeatingHandler(CreateGeminiResponse);
        using var httpClient = CreateHttpClient(handler);
        var client = new GeminiClient(null, httpClient);

        await InvokeLegacyAndHintedAsync(client);

        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
    }

    [Fact]
    public async Task DeepSeekAdapter_TransparentlyAcceptsHintAsNoOp() {
        var handler = new RepeatingHandler(CreateOpenAIChatResponse);
        using var httpClient = CreateHttpClient(handler);
        var client = new DeepSeekV4ChatClient(null, httpClient);

        await InvokeLegacyAndHintedAsync(client);

        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
    }

    private static async Task InvokeLegacyAndHintedAsync(ICompletionClient client) {
        CompletionRequest request = CreateRequest();
        _ = await client.StreamCompletionAsync(
            request,
            observer: null,
            CancellationToken.None
        );
        _ = await client.StreamCompletionAsync(
            request,
            new CompletionInvocationOptions {
                PromptCacheReuseHint = PromptCacheReuseHint.NoReuseExpected
            },
            observer: null,
            CancellationToken.None
        );
    }

    private static CompletionRequest CreateRequest() => new(
        ModelId: "model-a",
        SystemPrompt: "system",
        Context: [new ObservationMessage("hello")],
        Tools: []
    );

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) =>
        new(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

    private static HttpResponseMessage CreateOpenAIChatResponse() =>
        EventStreamResponse(
            """
            data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

            data: [DONE]

            """
        );

    private static HttpResponseMessage CreateOpenAIResponsesResponse() =>
        EventStreamResponse(
            """
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"ok"}

            event: response.completed
            data: {"type":"response.completed"}

            data: [DONE]

            """
        );

    private static HttpResponseMessage CreateGeminiResponse() =>
        EventStreamResponse(
            """
            data: {"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}

            """
            + "\n"
        );

    private static HttpResponseMessage EventStreamResponse(string body) =>
        new(HttpStatusCode.OK) {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

    private sealed class RepeatingHandler(
        Func<HttpResponseMessage> responseFactory
    ) : HttpMessageHandler {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            RequestBodies.Add(
                await request.Content!.ReadAsStringAsync(cancellationToken)
            );
            return responseFactory();
        }
    }
}
