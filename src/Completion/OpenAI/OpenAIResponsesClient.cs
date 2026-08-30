using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIResponsesClient : ICompletionClient {
    private const string DebugCategory = "Provider";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> ReservedRequestFieldNames = new(StringComparer.Ordinal) {
        "model",
        "instructions",
        "input",
        "tools",
        "tool_choice",
        "stream",
        "store",
        "include",
        "parallel_tool_calls",
        "reasoning"
    };

    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;
    private readonly OpenAIResponsesClientOptions _options;
    private readonly OpenAIResponsesProtocolClientCore _protocolCore;

    public string Name => _httpClient.BaseAddress?.Host ?? "openai";
    public string ApiSpecId =>
        PublicOpenAIResponsesProfile.ApiSpecId;

    public OpenAIResponsesClient(
        string? apiKey,
        HttpClient httpClient,
        OpenAIResponsesClientOptions? options = null
    ) {
        Atelia.Completion.ReasoningBlockCodecs.EnsureRegistered();

        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _httpClient = httpClient;
        _ = CompletionHttpRequestUtility.RequireConfiguredBaseAddress(_httpClient, nameof(OpenAIResponsesClient));
        options ??= new OpenAIResponsesClientOptions();
        _options = new OpenAIResponsesClientOptions {
            ReasoningEffort = options.ReasoningEffort,
            Store = options.Store,
            IncludeEncryptedReasoning = options.IncludeEncryptedReasoning,
            ExtraBody = options.ExtraBody is null
                ? null
                : (JsonObject)options.ExtraBody.DeepClone()
        };
        if (!Enum.IsDefined(_options.ReasoningEffort)) {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.ReasoningEffort,
                "Unknown reasoning effort."
            );
        }
        _protocolCore = new OpenAIResponsesProtocolClientCore(
            _options,
            ApiSpecId,
            "OpenAI/Responses",
            "OpenAI Responses",
            PublicOpenAIResponsesProfile.MapReasoningEffort,
            supportsRequiredNamedToolChoice: true,
            sanitizeProviderErrors: false
        );

        DebugUtil.Info(
            DebugCategory,
            $"[OpenAI/Responses] Client initialized base={_httpClient.BaseAddress}, extraBodyKeys={_options.ExtraBody?.Count ?? 0}, reasoningEffort={_options.ReasoningEffort}"
        );
    }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => StreamCompletionCoreAsync(
        request,
        CompletionInvocationOptions.Default,
        observer,
        cancellationToken
    );

    private async Task<CompletionResult> StreamCompletionCoreAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        return await _protocolCore.StreamCompletionAsync(
            this,
            request,
            invocationOptions,
            observer,
            SendStreamingRequestAsync,
            cancellationToken
        ).ConfigureAwait(false);
    }

    /// <summary>
    /// The current OpenAI Responses integration has no provider-neutral mapping
    /// for these hints, so validated values are accepted as an explicit no-op.
    /// </summary>
    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(invocationOptions);
        invocationOptions.Validate();
        return StreamCompletionCoreAsync(
            request,
            invocationOptions,
            observer,
            cancellationToken
        );
    }

    private async Task<HttpResponseMessage> SendStreamingRequestAsync(
        OpenAIResponsesApiRequest apiRequest,
        CancellationToken cancellationToken
    ) {
        return await CompletionHttpRequestUtility.SendStreamingRequestAsync(
            _httpClient,
            CreateHttpRequest(apiRequest),
            "OpenAI responses request",
            cancellationToken
        );
    }

    private HttpRequestMessage CreateHttpRequest(OpenAIResponsesApiRequest apiRequest) {
        apiRequest.ExtensionData = BuildExtraBodyExtensionData();
        var json = JsonSerializer.Serialize(apiRequest, SerializerOptions);
        DebugUtil.Trace(DebugCategory, $"[OpenAI/Responses] Request payload length={json.Length}");

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses") {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(_apiKey)) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private Dictionary<string, JsonElement>? BuildExtraBodyExtensionData() {
        if (_options.ExtraBody is null || _options.ExtraBody.Count == 0) { return null; }

        var extensionData = new Dictionary<string, JsonElement>(_options.ExtraBody.Count, StringComparer.Ordinal);
        foreach (var (propertyName, propertyValue) in _options.ExtraBody) {
            if (ReservedRequestFieldNames.Contains(propertyName)) {
                throw new InvalidOperationException(
                    $"OpenAI Responses extra body field '{propertyName}' collides with a reserved request property."
                );
            }

            extensionData[propertyName] = propertyValue is null
                ? JsonSerializer.SerializeToElement((object?)null, SerializerOptions)
                : propertyValue.Deserialize<JsonElement>(SerializerOptions);
        }

        return extensionData;
    }

}
