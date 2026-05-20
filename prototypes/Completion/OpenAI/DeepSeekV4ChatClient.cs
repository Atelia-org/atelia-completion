using Atelia.Completion.Abstractions;

namespace Atelia.Completion.OpenAI;

public sealed class DeepSeekV4ChatClient : ICompletionClient {
    private readonly OpenAIChatClient _inner;

    public string Name => _inner.Name;
    public string ApiSpecId => _inner.ApiSpecId;

    public DeepSeekV4ChatClient(
        string? apiKey,
        HttpClient httpClient,
        OpenAIChatClientOptions? options = null
    ) {
        _inner = new OpenAIChatClient(
            apiKey: apiKey,
            httpClient: httpClient,
            dialect: OpenAIChatDialects.DeepSeekV4,
            options: options
        );
    }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        return _inner.StreamCompletionAsync(request, observer, cancellationToken);
    }
}
