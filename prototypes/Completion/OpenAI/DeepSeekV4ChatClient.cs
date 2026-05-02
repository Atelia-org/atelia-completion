using Atelia.Completion.Abstractions;

namespace Atelia.Completion.OpenAI;

public sealed class DeepSeekV4ChatClient : ICompletionClient {
    private static readonly Uri DefaultBaseAddress = new("https://api.deepseek.com/");

    private readonly OpenAIChatClient _inner;

    public string Name => _inner.Name;
    public string ApiSpecId => _inner.ApiSpecId;

    public DeepSeekV4ChatClient(
        string? apiKey,
        HttpClient? httpClient = null,
        Uri? baseAddress = null,
        OpenAIChatClientOptions? options = null
    ) {
        _inner = new OpenAIChatClient(
            apiKey: apiKey,
            httpClient: httpClient,
            baseAddress: baseAddress ?? DefaultBaseAddress,
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
