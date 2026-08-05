using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIChatClient : ICompletionClient {
    private const string DebugCategory = "Provider";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> ReservedRequestFieldNames = new(StringComparer.Ordinal) {
        "model",
        "messages",
        "stream",
        "tools"
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly OpenAIChatDialect _dialect;
    private readonly JsonObject? _extraBody;

    public string Name => _httpClient.BaseAddress?.Host ?? "openai";
    public string ApiSpecId => "openai-chat-v1";

    public OpenAIChatClient(
        string? apiKey,
        HttpClient httpClient,
        OpenAIChatDialect? dialect = null,
        OpenAIChatClientOptions? options = null
    ) {
        Atelia.Completion.ReasoningBlockCodecs.EnsureRegistered();

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = CompletionHttpRequestUtility.RequireConfiguredBaseAddress(_httpClient, nameof(OpenAIChatClient));

        _dialect = dialect ?? OpenAIChatDialects.Strict;
        _extraBody = options?.ExtraBody is null ? null : (JsonObject)options.ExtraBody.DeepClone();

        DebugUtil.Info(
            DebugCategory,
            $"[OpenAI] Client initialized base={_httpClient.BaseAddress}, dialect={_dialect.Name}, extraBodyKeys={_extraBody?.Count ?? 0}"
        );
    }

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        DebugUtil.Info(DebugCategory, $"[OpenAI] Starting call model={request.ModelId}");

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(request, _dialect);
        using var response = await SendStreamingRequestAsync(apiRequest, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var invocation = CompletionDescriptor.From(this, request);
        var aggregator = new CompletionAggregator(invocation, observer);
        var parser = new OpenAIChatStreamParser(_dialect.WhitespaceContentMode, _dialect.ReasoningMode);
        var stoppedEarly = false;

        try {
            await foreach (var frame in CompletionSseEventReader.ReadFramesAsync(stream, cancellationToken)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Data is null) { continue; }

                if (string.Equals(frame.Data, "[DONE]", StringComparison.Ordinal)) {
                    CompletionStreamTermination.RequireTerminalEvent(
                        parser.TerminalEventObserved,
                        "OpenAI chat/completions"
                    );
                    break;
                }

                parser.ParseEvent(frame.Data, aggregator);
                if (parser.TerminalEventObserved) { break; }

                if (aggregator.ShouldStop) {
                    stoppedEarly = true;
                    break;
                }
            }

            if (stoppedEarly) {
                parser.DiscardIncompleteStreamingState();
                aggregator.AbortIncompleteStreamingState();
                aggregator.MarkIncomplete(detail: "Streaming observer stopped OpenAI chat completion early.");
            }
            else {
                CompletionStreamTermination.RequireTerminalEvent(
                    parser.TerminalEventObserved,
                    "OpenAI chat/completions"
                );
            }
        }
        catch {
            parser.DiscardIncompleteStreamingState();
            aggregator.AbortIncompleteStreamingState();
            throw;
        }

        DebugUtil.Trace(DebugCategory, "[OpenAI] Stream completed");
        return aggregator.Build();
    }

    private async Task<HttpResponseMessage> SendStreamingRequestAsync(OpenAIChatApiRequest apiRequest, CancellationToken cancellationToken) {
        return await CompletionHttpRequestUtility.SendStreamingRequestAsync(
            _httpClient,
            CreateHttpRequest(apiRequest),
            "OpenAI chat/completions request",
            cancellationToken
        );
    }

    private HttpRequestMessage CreateHttpRequest(OpenAIChatApiRequest apiRequest) {
        apiRequest.ExtensionData = BuildExtraBodyExtensionData();
        var json = JsonSerializer.Serialize(apiRequest, SerializerOptions);
        DebugUtil.Trace(DebugCategory, $"[OpenAI] Request payload length={json.Length}, dialect={_dialect.Name}");

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(_apiKey)) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private Dictionary<string, JsonElement>? BuildExtraBodyExtensionData() {
        if (_extraBody is null || _extraBody.Count == 0) { return null; }

        var extensionData = new Dictionary<string, JsonElement>(_extraBody.Count, StringComparer.Ordinal);

        foreach (var (propertyName, propertyValue) in _extraBody) {
            if (ReservedRequestFieldNames.Contains(propertyName)) {
                throw new InvalidOperationException(
                    $"OpenAI extra body field '{propertyName}' collides with a reserved request property."
                );
            }

            extensionData[propertyName] = propertyValue is null
                ? JsonSerializer.SerializeToElement((object?)null, SerializerOptions)
                : propertyValue.Deserialize<JsonElement>(SerializerOptions);
        }

        return extensionData;
    }
}
