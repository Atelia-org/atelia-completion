using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Atelia.Diagnostics;

namespace Atelia.Completion.Gemini;

public sealed class GeminiClient : ICompletionClient {
    private const string DebugCategory = "Provider";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;

    public string Name => _httpClient.BaseAddress?.Host ?? "generativelanguage.googleapis.com";
    public string ApiSpecId => "google-gemini-generate-content-v1beta";

    public GeminiClient(string? apiKey, HttpClient httpClient) {
        Atelia.Completion.ReasoningBlockCodecs.EnsureRegistered();

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = CompletionHttpRequestUtility.RequireConfiguredBaseAddress(_httpClient, nameof(GeminiClient));

        DebugUtil.Info(DebugCategory, $"[Gemini] Client initialized base={_httpClient.BaseAddress}");
    }

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        DebugUtil.Info(DebugCategory, $"[Gemini] Starting call model={request.ModelId}");

        var apiRequest = GeminiMessageConverter.ConvertToApiRequest(request);
        using var response = await SendStreamingRequestAsync(request.ModelId, apiRequest, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var invocation = CompletionDescriptor.From(this, request);
        var aggregator = new CompletionAggregator(invocation, observer);
        var parser = new GeminiStreamParser();
        var stoppedEarly = false;

        try {
            await foreach (var frame in CompletionSseEventReader.ReadFramesAsync(stream, cancellationToken)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Data is null) { continue; }

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
                aggregator.MarkIncomplete(detail: "Streaming observer stopped Gemini completion early.");
            }
            else {
                CompletionStreamTermination.RequireTerminalEvent(
                    parser.TerminalEventObserved,
                    "Gemini streamGenerateContent"
                );
            }
        }
        catch (Exception exception) {
            CleanupAfterFailure(parser, aggregator, exception);
            throw;
        }

        DebugUtil.Trace(DebugCategory, "[Gemini] Stream completed");
        return aggregator.Build();
    }

    private async Task<HttpResponseMessage> SendStreamingRequestAsync(
        string modelId,
        GeminiGenerateContentRequest apiRequest,
        CancellationToken cancellationToken
    ) {
        return await CompletionHttpRequestUtility.SendStreamingRequestAsync(
            _httpClient,
            CreateHttpRequest(modelId, apiRequest),
            "Gemini streamGenerateContent request",
            cancellationToken
        );
    }

    private HttpRequestMessage CreateHttpRequest(string modelId, GeminiGenerateContentRequest apiRequest) {
        var json = JsonSerializer.Serialize(apiRequest, SerializerOptions);
        DebugUtil.Trace(DebugCategory, $"[Gemini] Request payload length={json.Length}");

        var modelPath = NormalizeModelPath(modelId);
        var relativeUri = $"v1beta/{modelPath}:streamGenerateContent?alt=sse";

        var request = new HttpRequestMessage(HttpMethod.Post, relativeUri) {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(_apiKey)) {
            request.Headers.Add("x-goog-api-key", _apiKey);
        }

        return request;
    }

    private static string NormalizeModelPath(string modelId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var normalized = modelId.StartsWith("models/", StringComparison.Ordinal)
            ? modelId
            : $"models/{modelId}";

        if (!normalized.StartsWith("models/", StringComparison.Ordinal)) { return normalized; }

        var suffix = normalized["models/".Length..];
        return $"models/{Uri.EscapeDataString(suffix)}";
    }

    private static void CleanupAfterFailure(
        GeminiStreamParser parser,
        CompletionAggregator aggregator,
        Exception originalException
    ) {
        try {
            parser.DiscardIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[Gemini] Parser cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }

        try {
            aggregator.AbortIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[Gemini] Observer cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }
    }
}
