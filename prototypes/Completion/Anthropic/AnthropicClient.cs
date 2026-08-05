using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atelia.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;

namespace Atelia.Completion.Anthropic;

/// <summary>
/// Anthropic Messages API 客户端实现。
/// 规范：https://docs.anthropic.com/claude/reference/messages_post
/// </summary>
public sealed class AnthropicClient : ICompletionClient {
    private const string DebugCategory = "Provider";
    private const string DefaultApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly string _apiVersion;
    private readonly int? _defaultMaxTokens;
    private readonly bool _enablePromptCaching;

    public string Name => _httpClient.BaseAddress?.Host ?? "anthropic";
    public string ApiSpecId => "messages-v1";

    public AnthropicClient(string? apiKey, HttpClient httpClient, string? apiVersion = null, int? defaultMaxTokens = null, bool enablePromptCaching = true) {
        Atelia.Completion.ReasoningBlockCodecs.EnsureRegistered();

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = CompletionHttpRequestUtility.RequireConfiguredBaseAddress(_httpClient, nameof(AnthropicClient));

        _apiVersion = string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion;
        _defaultMaxTokens = defaultMaxTokens;
        _enablePromptCaching = enablePromptCaching;

        DebugUtil.Info(DebugCategory, $"[Anthropic] Client initialized base={_httpClient.BaseAddress}, version={_apiVersion}, defaultMaxTokens={_defaultMaxTokens?.ToString() ?? "(none)"}, promptCaching={_enablePromptCaching}");
    }

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        DebugUtil.Info(DebugCategory, $"[Anthropic] Starting call model={request.ModelId}");

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request, _defaultMaxTokens, _enablePromptCaching);
        using var response = await SendStreamingRequestAsync(apiRequest, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var invocation = CompletionDescriptor.From(this, request);
        var aggregator = new CompletionAggregator(invocation, observer);
        var parser = new AnthropicStreamParser();
        var stoppedEarly = false;

        try {
            await foreach (var frame in CompletionSseEventReader.ReadFramesAsync(stream, cancellationToken)) {
                cancellationToken.ThrowIfCancellationRequested();

                parser.ParseEvent(frame.Data, aggregator, frame.EventType);
                if (parser.TerminalEventObserved) { break; }

                if (aggregator.ShouldStop) {
                    stoppedEarly = true;
                    break;
                }
            }

            if (stoppedEarly) {
                parser.DiscardIncompleteStreamingState();
                aggregator.AbortIncompleteStreamingState();
                aggregator.MarkIncomplete(detail: "Streaming observer stopped Anthropic completion early.");
            }
            else {
                CompletionStreamTermination.RequireTerminalEvent(
                    parser.TerminalEventObserved,
                    "Anthropic Messages"
                );
            }
        }
        catch (Exception exception) {
            CleanupAfterFailure(parser, aggregator, exception);
            throw;
        }

        DebugUtil.Trace(DebugCategory, "[Anthropic] Stream completed");
        return aggregator.Build();
    }

    private async Task<HttpResponseMessage> SendStreamingRequestAsync(AnthropicApiRequest apiRequest, CancellationToken cancellationToken) {
        return await CompletionHttpRequestUtility.SendStreamingRequestAsync(
            _httpClient,
            CreateHttpRequest(apiRequest),
            "Anthropic messages request",
            cancellationToken
        );
    }

    private HttpRequestMessage CreateHttpRequest(AnthropicApiRequest apiRequest) {
        var json = JsonSerializer.Serialize(apiRequest, SerializerOptions);
        DebugUtil.Trace(DebugCategory, $"[Anthropic] Request payload length={json.Length}");

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages") {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(_apiKey)) {
            request.Headers.Add("x-api-key", _apiKey);
        }

        if (!string.IsNullOrWhiteSpace(_apiVersion)) {
            request.Headers.Add("anthropic-version", _apiVersion);
        }

        return request;
    }

    private static void CleanupAfterFailure(
        AnthropicStreamParser parser,
        CompletionAggregator aggregator,
        Exception originalException
    ) {
        try {
            parser.DiscardIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[Anthropic] Parser cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }

        try {
            aggregator.AbortIncompleteStreamingState();
        }
        catch (Exception cleanupException) {
            DebugUtil.Warning(
                DebugCategory,
                $"[Anthropic] Observer cleanup failed while preserving {originalException.GetType().Name}.",
                cleanupException
            );
        }
    }
}
