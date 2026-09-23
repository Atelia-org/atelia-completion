using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Completion.Anthropic.Tests;

/// <summary>
/// Opt-in production-client acceptance of an Anthropic cross-model thinking
/// request. The relay owns model/signature compatibility decisions.
/// </summary>
public sealed class AnthropicCrossModelThinkingLiveTests(ITestOutputHelper output) {
    private const string EnableEnv = "ATELIA_RUN_ANTHROPIC_46_TO_55_THINKING_LIVE";
    private const string SourceModel = "claude-opus-4-6";
    private const string TargetModel = "claude-opus-5-5";

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_ProductionClientReplaysOpus46ThinkingToOpus55() {
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
        CompletionResult first = await CallAsync(client,
            NewRequest(SourceModel, system, [firstUser]));
        Assert.Equal(CompletionTerminationKind.Completed, first.Termination.Kind);
        Assert.Equal(SourceModel, first.Invocation.Model);
        AnthropicReasoningBlock firstReasoning = Assert.Single(
            first.Message.Blocks.OfType<AnthropicReasoningBlock>());
        var firstThinking = Assert.IsType<AnthropicThinkingBlock>(
            AnthropicThinkingPayloadCodec.Decode(firstReasoning.OpaquePayload));

        CompletionResult second = await CallAsync(client,
            NewRequest(TargetModel, system, [
                firstUser,
                first.Message,
                new ObservationMessage("What product did I ask for? Give only the number.")
            ]));
        Assert.Equal(CompletionTerminationKind.Completed, second.Termination.Kind);
        Assert.Equal(TargetModel, second.Invocation.Model);
        Assert.Contains("323", second.Message.GetFlattenedText(), StringComparison.Ordinal);

        Assert.Equal(2, probe.Exchanges.Count);
        ExchangeSummary seed = probe.Exchanges[0];
        ExchangeSummary continuation = probe.Exchanges[1];
        output.WriteLine($"sourceHttp={seed.StatusCode} sourceModel={seed.ResponseModel} "
            + $"continuationHttp={continuation.StatusCode} targetModel={continuation.ResponseModel} "
            + $"replayedThinking={continuation.ReplayedThinkingCount} "
            + $"sameSignedPayload={firstThinking.Thinking == continuation.ReplayedThinking && firstThinking.Signature == continuation.ReplayedSignature} "
            + $"stablePrefix={seed.PrefixHash == continuation.PrefixHash} "
            + $"terminal={continuation.HasMessageStop} transformationsPresent={continuation.TransformationsPresent} "
            + $"transformations={continuation.TransformationCount}");

        Assert.Equal(200, seed.StatusCode);
        Assert.Equal(200, continuation.StatusCode);
        Assert.Equal(SourceModel, seed.ResponseModel);
        Assert.Equal(TargetModel, continuation.ResponseModel);
        Assert.Equal(1, continuation.ReplayedThinkingCount);
        Assert.Equal(firstThinking.Thinking, continuation.ReplayedThinking);
        Assert.Equal(firstThinking.Signature, continuation.ReplayedSignature);
        Assert.Equal(seed.PrefixHash, continuation.PrefixHash);
        Assert.True(continuation.HasMessageStop);
    }

    private static CompletionRequest NewRequest(string model, string system,
        IReadOnlyList<IHistoryMessage> messages) => new(
        model,
        new CompletionPromptPrefix(system, CompletionOutputContract.ProviderDefault([]), messages),
        []);

    private static async Task<CompletionResult> CallAsync(
        AnthropicClient client, CompletionRequest request) {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        return await client.StreamCompletionAsync(request, observer: null, deadline.Token);
    }

    private sealed class MetadataProbe : ICompletionHttpExchangeSink {
        public List<ExchangeSummary> Exchanges { get; } = [];

        public void OnExchange(CompletionHttpExchange exchange) {
            if (exchange.Method != "POST" || exchange.RequestText is null) { return; }
            using JsonDocument request = JsonDocument.Parse(exchange.RequestText);
            JsonElement root = request.RootElement;
            JsonElement messages = root.GetProperty("messages");
            JsonElement? replayed = messages.EnumerateArray()
                .SelectMany(static message => message.GetProperty("content").EnumerateArray())
                .Where(static block => block.TryGetProperty("type", out var type)
                    && type.GetString() == "thinking")
                .Select(static block => (JsonElement?)block)
                .FirstOrDefault();
            int replayedCount = messages.EnumerateArray()
                .SelectMany(static message => message.GetProperty("content").EnumerateArray())
                .Count(static block => block.TryGetProperty("type", out var type)
                    && type.GetString() == "thinking");
            string prefix = root.GetProperty("system").GetRawText()
                + (root.TryGetProperty("tools", out var tools) ? tools.GetRawText() : "null")
                + messages[0].GetRawText();
            string prefixHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(prefix)));

            string? responseModel = null;
            bool hasMessageStop = false;
            bool transformationsPresent = false;
            int transformationCount = 0;
            if (exchange.ResponseText is string responseText) {
                foreach (string line in responseText.Split('\n')) {
                    if (!line.StartsWith("data: {", StringComparison.Ordinal)) { continue; }
                    using JsonDocument frame = JsonDocument.Parse(line[6..]);
                    JsonElement eventRoot = frame.RootElement;
                    if (eventRoot.TryGetProperty("input_transformations", out var rootItems)) {
                        RecordTransformations(rootItems);
                    }
                    string? eventType = eventRoot.TryGetProperty("type", out var type)
                        ? type.GetString() : null;
                    if (eventType == "message_start"
                        && eventRoot.TryGetProperty("message", out var responseMessage)) {
                        responseModel = responseMessage.TryGetProperty("model", out var declared)
                            ? declared.GetString() : null;
                        if (responseMessage.TryGetProperty("input_transformations", out var items)) {
                            RecordTransformations(items);
                        }
                    }
                    else if (eventType == "message_delta"
                        && eventRoot.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("input_transformations", out var items)) {
                        RecordTransformations(items);
                    }
                    else if (eventType == "message_stop") { hasMessageStop = true; }
                }
            }

            Exchanges.Add(new ExchangeSummary(exchange.StatusCode, responseModel,
                replayedCount,
                replayed is JsonElement block ? block.GetProperty("thinking").GetString() : null,
                replayed is JsonElement signed ? signed.GetProperty("signature").GetString() : null,
                prefixHash, hasMessageStop, transformationsPresent, transformationCount));
            void RecordTransformations(JsonElement items) {
                if (items.ValueKind != JsonValueKind.Array) { return; }
                transformationsPresent = true;
                transformationCount = items.GetArrayLength();
            }
        }
    }

    private sealed record ExchangeSummary(
        int? StatusCode,
        string? ResponseModel,
        int ReplayedThinkingCount,
        string? ReplayedThinking,
        string? ReplayedSignature,
        string PrefixHash,
        bool HasMessageStop,
        bool TransformationsPresent,
        int TransformationCount);
}
