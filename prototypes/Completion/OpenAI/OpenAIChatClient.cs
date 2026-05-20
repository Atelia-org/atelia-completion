using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
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
        ArgumentNullException.ThrowIfNull(httpClient);

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = _httpClient.BaseAddress ?? throw new InvalidOperationException(
            "OpenAIChatClient requires HttpClient.BaseAddress to be configured by the caller."
        );

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
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var invocation = CompletionDescriptor.From(this, request);
        var aggregator = new CompletionAggregator(invocation, observer);
        var parser = new OpenAIChatStreamParser(_dialect.WhitespaceContentMode, _dialect.ReasoningMode);
        string? line;
        var stoppedEarly = false;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null) {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line)) { continue; }
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) { continue; }

            var json = line["data: ".Length..];
            if (json == "[DONE]") { break; }

            parser.ParseEvent(json, aggregator);
            if (aggregator.ShouldStop) {
                stoppedEarly = true;
                break;
            }
        }

        if (stoppedEarly) {
            parser.DiscardIncompleteStreamingState();
            aggregator.AbortIncompleteStreamingState();
        }
        else {
            parser.Complete(aggregator);
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
