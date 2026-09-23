using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Completion.Anthropic.Tests;

/// <summary>
/// Opt-in relay experiment. The 5.5 requests bypass only the converter's
/// production Origin gate, so inconclusive evidence cannot loosen that gate.
/// </summary>
public sealed class AnthropicCrossModelThinkingLiveTests(ITestOutputHelper output) {
    private const string EnableEnv = "ATELIA_RUN_ANTHROPIC_46_TO_55_THINKING_LIVE";
    private const string SourceModel = "claude-opus-4-6";
    private const string TargetModel = "claude-opus-5-5";
    private static readonly JsonSerializerOptions WireOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_Opus46ThinkingIsReadByOpus55AndPrefixEditsAreDetected() {
        if (Environment.GetEnvironmentVariable(EnableEnv) != "1") { return; }

        string baseUrl = Environment.GetEnvironmentVariable("CLAUDE_BASE_URL")
            ?? throw new InvalidOperationException("CLAUDE_BASE_URL is required.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseAddress)
            || baseAddress.Scheme != Uri.UriSchemeHttps
            || baseAddress.UserInfo.Length != 0
            || baseAddress.Query.Length != 0
            || baseAddress.Fragment.Length != 0) {
            throw new InvalidOperationException("CLAUDE_BASE_URL must be an HTTPS API base URL.");
        }
        string apiKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY")
            ?? throw new InvalidOperationException("CLAUDE_API_KEY is required.");
        if (string.IsNullOrWhiteSpace(apiKey)) {
            throw new InvalidOperationException("CLAUDE_API_KEY must not be blank.");
        }

        var probe = new MetadataProbe();
        using var httpClient = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(new HttpClientHandler())
            .AddExchangeSink(probe)
            .Build();
        httpClient.BaseAddress = CompletionHttpRequestUtility.NormalizeBaseAddress(baseAddress);
        httpClient.DefaultRequestHeaders.Add(
            "anthropic-beta", "thinking-binding-controls-2026-08-01");
        var client = new AnthropicClient(apiKey, httpClient,
            enablePromptCaching: false,
            reasoningEffort: CompletionReasoningEffort.High);

        const string system = "Answer briefly. Preserve the earlier conversation exactly.";
        var firstUser = new ObservationMessage("Calculate 17 times 19. Give only the number.");
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3))) {
            CompletionResult first = await client.StreamCompletionAsync(
                NewRequest(SourceModel, system, [firstUser]),
                observer: null, deadline.Token);
            Assert.Equal(CompletionTerminationKind.Completed, first.Termination.Kind);
            Assert.Equal(SourceModel, first.Invocation.Model);
            AnthropicReasoningBlock firstReasoning = Assert.Single(
                first.Message.Blocks.OfType<AnthropicReasoningBlock>());
            var firstThinking = Assert.IsType<AnthropicThinkingBlock>(
                AnthropicThinkingPayloadCodec.Decode(firstReasoning.OpaquePayload));
            Assert.False(string.IsNullOrWhiteSpace(first.Message.GetFlattenedText()));

            var secondUser = new ObservationMessage("What product did I ask for? Give only the number.");
            int continuationStatus = await SendExperimentalAsync(httpClient, apiKey,
                NewRequest(TargetModel, system, [firstUser, first.Message, secondUser]));
            int editedStatus = await SendExperimentalAsync(httpClient, apiKey,
                NewRequest(TargetModel, system + " Edited.",
                    [firstUser, first.Message, new ObservationMessage("Repeat the product.")]));
            var visibleAction = new ActionMessage(first.Message.Blocks
                .Where(static block => block is not AnthropicReasoningBlock).ToArray());
            int strippedStatus = await SendExperimentalAsync(httpClient, apiKey,
                NewRequest(TargetModel, system, [firstUser, visibleAction, secondUser]));

            Assert.Equal(4, probe.Exchanges.Count);
            ExchangeSummary source = probe.Exchanges[0];
            ExchangeSummary continuation = probe.Exchanges[1];
            ExchangeSummary edited = probe.Exchanges[2];
            ExchangeSummary stripped = probe.Exchanges[3];
            bool editedMismatch = edited.StatusCode == 400 && edited.PrefixMismatchError
                || edited.StatusCode == 200 && edited.Transformations.Any(
                    static item => item.Reason == "prefix_binding_mismatch");

            output.WriteLine($"sourceHttp={source.StatusCode} sourceThinking={first.Message.Blocks.OfType<AnthropicReasoningBlock>().Count()} "
                + $"thinkingChars={firstThinking.Thinking.Length} payloadBytes={firstReasoning.OpaquePayload.Length} "
                + $"continuationHttp={continuationStatus} replayedThinking={continuation.ReplayedThinkingBlockCount} "
                + $"continuationTerminal={continuation.HasMessageStop} transformationsPresent={continuation.TransformationsPresent} "
                + $"transformations={continuation.Transformations.Count} stablePrefix={source.PrefixHash == continuation.PrefixHash} "
                + $"editedHttp={editedStatus} editedPrefixDiffers={source.PrefixHash != edited.PrefixHash} "
                + $"editedMismatch={editedMismatch} strippedHttp={strippedStatus} "
                + $"strippedThinking={stripped.ReplayedThinkingBlockCount} "
                + $"withThinkingInput={continuation.InputTokens} withoutThinkingInput={stripped.InputTokens}");

            Assert.Equal(200, source.StatusCode);
            Assert.Equal(200, continuationStatus);
            Assert.Equal(200, strippedStatus);
            Assert.Equal(SourceModel, source.ResponseModel);
            Assert.Equal(TargetModel, continuation.ResponseModel);
            Assert.True(continuation.HasMessageStop);
            Assert.True(continuation.ReplayedThinkingBlockCount > 0);
            Assert.Equal(firstThinking.Thinking, continuation.ReplayedThinking);
            Assert.Equal(firstThinking.Signature, continuation.ReplayedSignature);
            Assert.Equal(0, stripped.ReplayedThinkingBlockCount);
            Assert.Equal(source.PrefixHash, continuation.PrefixHash);
            Assert.NotEqual(source.PrefixHash, edited.PrefixHash);
            Assert.True(continuation.TransformationsPresent,
                "The relay omitted input_transformations; HTTP 200 cannot establish that 5.5 read earlier thinking.");
            Assert.Empty(continuation.Transformations);
            Assert.True(editedMismatch,
                "The relay did not report the deliberate prefix edit as a binding mismatch.");
        }
    }

    private static CompletionRequest NewRequest(string model, string system,
        IReadOnlyList<IHistoryMessage> messages) => new(
        model,
        new CompletionPromptPrefix(system, CompletionOutputContract.ProviderDefault([]), messages),
        []);

    private static async Task<int> SendExperimentalAsync(
        HttpClient httpClient, string apiKey, CompletionRequest request) {
        // Bypass only the Origin equality check. Projection and signed payload
        // serialization are otherwise the same as the production client.
        AnthropicApiRequest projected = AnthropicMessageConverter.ConvertToApiRequest(
            request, modelMaximumTokens: 128_000,
            enablePromptCaching: false,
            reasoningEffort: CompletionReasoningEffort.High,
            targetInvocation: null);
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/messages") {
            Content = new StringContent(JsonSerializer.Serialize(projected, WireOptions),
                Encoding.UTF8, "application/json")
        };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");
        message.Headers.Accept.ParseAdd("text/event-stream");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using HttpResponseMessage response = await httpClient.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        _ = await response.Content.ReadAsStringAsync(deadline.Token);
        return (int)response.StatusCode;
    }

    private sealed class MetadataProbe : ICompletionHttpExchangeSink {
        public List<ExchangeSummary> Exchanges { get; } = [];

        public void OnExchange(CompletionHttpExchange exchange) {
            if (exchange.Method != "POST" || exchange.RequestText is null) { return; }
            using JsonDocument request = JsonDocument.Parse(exchange.RequestText);
            JsonElement root = request.RootElement;
            JsonElement messages = root.GetProperty("messages");
            int replayed = messages.EnumerateArray()
                .SelectMany(static message => message.GetProperty("content").EnumerateArray())
                .Count(static block => block.TryGetProperty("type", out var type)
                    && type.GetString() is "thinking" or "redacted_thinking");
            JsonElement? replayedThinking = messages.EnumerateArray()
                .SelectMany(static message => message.GetProperty("content").EnumerateArray())
                .Where(static block => block.TryGetProperty("type", out var type)
                    && type.GetString() == "thinking")
                .Select(static block => (JsonElement?)block)
                .FirstOrDefault();
            string prefix = root.GetProperty("system").GetRawText()
                + (root.TryGetProperty("tools", out var tools) ? tools.GetRawText() : "null")
                + messages[0].GetRawText();
            string prefixHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(prefix)));
            string? responseModel = null;
            bool transformationsPresent = false;
            List<Transformation> transformations = [];
            bool hasMessageStop = false;
            long? inputTokens = null;
            bool prefixMismatchError = false;
            if (exchange.ResponseText is string responseText) {
                foreach (string line in responseText.Split('\n')) {
                    if (!line.StartsWith("data: {", StringComparison.Ordinal)) { continue; }
                    using JsonDocument eventDocument = JsonDocument.Parse(line[6..]);
                    JsonElement eventRoot = eventDocument.RootElement;
                    string? eventType = eventRoot.TryGetProperty("type", out var type)
                        ? type.GetString() : null;
                    if (eventRoot.TryGetProperty("input_transformations", out var rootItems)) {
                        transformationsPresent = true;
                        transformations = ReadTransformations(rootItems);
                    }
                    if (eventType == "message_start"
                        && eventRoot.TryGetProperty("message", out var responseMessage)) {
                        responseModel = responseMessage.TryGetProperty("model", out var declared)
                            ? declared.GetString() : null;
                        if (responseMessage.TryGetProperty("usage", out var usage)
                            && usage.TryGetProperty("input_tokens", out var count)) {
                            inputTokens = count.GetInt64();
                        }
                        if (responseMessage.TryGetProperty("input_transformations", out var items)) {
                            transformationsPresent = true;
                            transformations = ReadTransformations(items);
                        }
                    }
                    else if (eventType == "message_delta"
                        && eventRoot.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("input_transformations", out var items)) {
                        transformationsPresent = true;
                        transformations = ReadTransformations(items);
                    }
                    else if (eventType == "message_stop") { hasMessageStop = true; }
                }
                if (exchange.StatusCode == 400) {
                    prefixMismatchError = responseText.Contains("prefix", StringComparison.OrdinalIgnoreCase)
                        && responseText.Contains("signature", StringComparison.OrdinalIgnoreCase);
                }
            }
            Exchanges.Add(new ExchangeSummary(exchange.StatusCode,
                root.GetProperty("model").GetString(), responseModel,
                replayed,
                replayedThinking is JsonElement block
                    ? block.GetProperty("thinking").GetString() : null,
                replayedThinking is JsonElement signedBlock
                    ? signedBlock.GetProperty("signature").GetString() : null,
                prefixHash, transformationsPresent, transformations,
                hasMessageStop, inputTokens, prefixMismatchError));
        }

        private static List<Transformation> ReadTransformations(JsonElement items) =>
            items.EnumerateArray()
                .Select(static item => new Transformation(
                    item.GetProperty("type").GetString(),
                    item.GetProperty("reason").GetString()))
                .ToList();
    }

    private sealed record Transformation(string? Type, string? Reason);
    private sealed record ExchangeSummary(
        int? StatusCode,
        string? RequestModel,
        string? ResponseModel,
        int ReplayedThinkingBlockCount,
        string? ReplayedThinking,
        string? ReplayedSignature,
        string PrefixHash,
        bool TransformationsPresent,
        List<Transformation> Transformations,
        bool HasMessageStop,
        long? InputTokens,
        bool PrefixMismatchError);
}
