using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIChatClient : ICompletionClient {
    private const string DebugCategory = "Provider";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly OpenAIChatDialect _dialect;

    public string Name => _httpClient.BaseAddress?.Host ?? "openai";
    public string ApiSpecId => "openai-chat-v1";

    public OpenAIChatClient(
        string? apiKey,
        HttpClient? httpClient = null,
        Uri? baseAddress = null,
        OpenAIChatDialect? dialect = null
    ) {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient ?? new HttpClient();

        // 显式 baseAddress 永远胜出；否则尊重外部 HttpClient 已配置的 BaseAddress；都没有时回落到官方端点。
        // 不要无条件覆盖：HttpClient.BaseAddress 在已发出首个请求后再赋值会抛 InvalidOperationException，
        // 且共享 HttpClient 的调用方也会被静默改写意图。
        if (baseAddress is not null) {
            _httpClient.BaseAddress = baseAddress;
        }
        else if (_httpClient.BaseAddress is null) {
            _httpClient.BaseAddress = new Uri("https://api.openai.com/");
        }

        _dialect = dialect ?? OpenAIChatDialects.Strict;

        DebugUtil.Info(DebugCategory, $"[OpenAI] Client initialized base={_httpClient.BaseAddress}, dialect={_dialect.Name}");
    }

    public async Task<AggregatedAction> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        DebugUtil.Info(DebugCategory, $"[OpenAI] Starting call model={request.ModelId}");

        var apiRequest = OpenAIChatMessageConverter.ConvertToApiRequest(request, _dialect);
        if (ShouldRequestStreamUsage()) {
            apiRequest.StreamOptions = new OpenAIChatStreamOptions { IncludeUsage = true };
        }

        var streamingRequest = await SendStreamingRequestAsync(apiRequest, cancellationToken);
        using var response = streamingRequest.Response;
        var shouldExpectUsagePayload = streamingRequest.UsageRequested;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var invocation = CompletionDescriptor.From(this, request);
        var aggregator = new CompletionAggregator(invocation, observer);
        var parser = new OpenAIChatStreamParser(request.Tools, _dialect.WhitespaceContentMode);
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

        if (!stoppedEarly && parser.GetFinalUsage() is { } usage) {
            aggregator.AppendTokenUsage(usage);
        }
        else if (!stoppedEarly && shouldExpectUsagePayload) {
            DebugUtil.Warning(DebugCategory, "[OpenAI] Stream completed without usage payload");
        }

        DebugUtil.Trace(DebugCategory, "[OpenAI] Stream completed");
        return aggregator.Build();
    }

    private async Task<StreamingRequestResult> SendStreamingRequestAsync(OpenAIChatApiRequest apiRequest, CancellationToken cancellationToken) {
        var requestBody = apiRequest;
        var includeUsage = requestBody.StreamOptions?.IncludeUsage == true;

        while (true) {
            using var httpRequest = CreateHttpRequest(requestBody);
            var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode) {
                return new StreamingRequestResult(response, includeUsage);
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = response.StatusCode;

            if (ShouldRetryWithoutStreamOptions(includeUsage, statusCode, errorBody)) {
                DebugUtil.Warning(
                    DebugCategory,
                    $"[OpenAI] Provider rejected stream_options, retrying without include_usage status={(int)statusCode}, dialect={_dialect.Name}"
                );

                response.Dispose();
                requestBody = new OpenAIChatApiRequest {
                    Model = requestBody.Model,
                    Messages = requestBody.Messages,
                    Stream = requestBody.Stream,
                    StreamOptions = null,
                    Tools = requestBody.Tools
                };
                includeUsage = false;
                continue;
            }

            response.Dispose();
            throw CreateRequestFailure(statusCode, errorBody);
        }
    }

    private HttpRequestMessage CreateHttpRequest(OpenAIChatApiRequest apiRequest) {
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

    private bool ShouldRequestStreamUsage() {
        return _dialect.StreamUsageMode is not OpenAIChatStreamUsageMode.Disabled;
    }

    private bool ShouldRetryWithoutStreamOptions(bool includeUsageRequested, HttpStatusCode statusCode, string errorBody) {
        if (!includeUsageRequested) { return false; }
        if (_dialect.StreamUsageMode is not OpenAIChatStreamUsageMode.RequestUsageAndRetryWithoutStreamOptions) { return false; }

        if (statusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableContent)) {
            return false;
        }

        if (string.IsNullOrWhiteSpace(errorBody)) { return false; }

        return errorBody.Contains("stream_options", StringComparison.OrdinalIgnoreCase)
            || errorBody.Contains("include_usage", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestException CreateRequestFailure(HttpStatusCode statusCode, string errorBody) {
        var normalizedBody = string.IsNullOrWhiteSpace(errorBody)
            ? "<empty>"
            : errorBody.ReplaceLineEndings(" ").Trim();

        if (normalizedBody.Length > 512) {
            normalizedBody = normalizedBody[..512];
        }

        return new HttpRequestException(
            $"OpenAI chat/completions request failed status={(int)statusCode} body={normalizedBody}",
            inner: null,
            statusCode: statusCode
        );
    }

    private sealed record StreamingRequestResult(HttpResponseMessage Response, bool UsageRequested);
}
