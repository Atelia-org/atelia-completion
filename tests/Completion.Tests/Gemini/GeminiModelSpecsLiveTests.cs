using System.Net;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Completion.Gemini.Tests;

/// <summary>Opt-in production-client checks; only content-free observations are logged.</summary>
public sealed class GeminiModelSpecsLiveTests(ITestOutputHelper output) {
    private const string EnableEnv = "ATELIA_RUN_GEMINI_MODEL_SPECS_LIVE";

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_FlashLiteDisabled_UsesLowAndBuiltinMaximum() {
        if (Environment.GetEnvironmentVariable(EnableEnv) != "1") { return; }
        using var probe = new ProbeHandler(new HttpClientHandler());
        using var http = CreateHttp(probe);
        var client = CreateClient(http);
        var request = Request("gemini-3.5-flash-lite", CompletionOutputContract.ProviderDefault([]),
            [new ObservationMessage("Calculate 17 times 19. Reply with just the decimal product.")]);
        CompletionResult result = await Call(client, request, probe, "lite-text");
        Assert.Equal("323", result.Message.GetFlattenedText().Trim());
    }

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_FlashForcedToolAndContinuation_PreserveSignatureAndResponseId() {
        if (Environment.GetEnvironmentVariable(EnableEnv) != "1") { return; }
        using var probe = new ProbeHandler(new HttpClientHandler());
        using var http = CreateHttp(probe);
        var client = CreateClient(http);
        var tool = new ToolDefinition("report_product", "Report the computed integer product.",
            new ToolSchema.Object(properties: [
                new ToolSchema.Property("product", new ToolSchema.Value(ToolParamType.Int32), isRequired: true)
            ]));
        var user = new ObservationMessage("Calculate 17 times 19 and call report_product once with the product.");
        CompletionResult first = await Call(client, Request("gemini-3.8-flash",
            new CompletionOutputContract([tool], CompletionToolChoice.RequiredNamed(tool.Name)), [user]), probe, "flash-tool");
        RawToolCall call = Assert.Single(first.Message.ToolCalls);
        Assert.Equal(tool.Name, call.ToolName);
        using JsonDocument args = JsonDocument.Parse(call.RawArgumentsJson);
        Assert.Equal(323, args.RootElement.GetProperty("product").GetInt32());
        var replay = Assert.Single(first.Message.Blocks.OfType<GeminiReplayBlock>());
        string[] signatures = Signatures(replay.OpaquePayload);
        Assert.NotEmpty(signatures);

        CompletionResult second = await Call(client, Request("gemini-3.8-flash",
            new CompletionOutputContract([tool], CompletionToolChoice.None), [
                user, first.Message,
                new ToolResultsMessage(null, [ToolResult.FromText(tool.Name, call.ToolCallId,
                    ToolExecutionStatus.Success, "SPECS_OK")]),
                new ObservationMessage("Reply with just the tool result marker.")
            ]), probe, "flash-continuation");
        Assert.Equal(signatures, probe.ReplayedSignatures);
        Assert.True(probe.ResponseIdentityMatched);
        Assert.Equal("SPECS_OK", second.Message.GetFlattenedText().Trim());
        output.WriteLine("toolContinuationCompleted=true signaturePreserved=true functionResponseIdMatched=true finalUserTextPresent=true");
    }

    private async Task<CompletionResult> Call(GeminiClient client, CompletionRequest request, ProbeHandler probe, string stage) {
        probe.Reset();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CompletionResult result = await client.StreamCompletionAsync(request, null, deadline.Token);
        output.WriteLine($"stage={stage} model={request.ModelId} GET={probe.Gets} POST={probe.Posts} "
            + $"http={(int?)probe.Status} maxOutputTokens={probe.Maximum} thinkingLevel={probe.Level} "
            + $"terminal={result.Termination.Kind} outputTokens={result.Usage.OutputTokens?.ToString() ?? "unknown"}");
        Assert.Equal(0, probe.Gets);
        Assert.Equal(1, probe.Posts);
        Assert.Equal(HttpStatusCode.OK, probe.Status);
        Assert.Equal(65_536, probe.Maximum);
        Assert.Equal("low", probe.Level);
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        return result;
    }

    private static GeminiClient CreateClient(HttpClient http) {
        string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { throw new InvalidOperationException("GEMINI_API_KEY must be set for this opt-in test."); }
        return new GeminiClient(key, http, new GeminiClientOptions { ReasoningEffort = CompletionReasoningEffort.Disabled });
    }

    private static HttpClient CreateHttp(HttpMessageHandler handler) => CompletionHttpTransportFactory.CreateLiveClient(
        new Uri("https://generativelanguage.googleapis.com/"), handler);

    private static CompletionRequest Request(string model, CompletionOutputContract contract, IReadOnlyList<IHistoryMessage> messages) =>
        new(model, new CompletionPromptPrefix("Follow the user's requested format.", contract, messages), []);

    private static string[] Signatures(ReadOnlyMemory<byte> payload) {
        using JsonDocument doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("parts").EnumerateArray()
            .Where(p => p.TryGetProperty("thoughtSignature", out _))
            .Select(p => p.GetProperty("thoughtSignature").GetString()!).ToArray();
    }

    private sealed class ProbeHandler(HttpMessageHandler inner) : DelegatingHandler(inner) {
        public int Gets { get; private set; }
        public int Posts { get; private set; }
        public HttpStatusCode? Status { get; private set; }
        public int Maximum { get; private set; }
        public string? Level { get; private set; }
        public string[] ReplayedSignatures { get; private set; } = [];
        public bool ResponseIdentityMatched { get; private set; }

        public void Reset() {
            Gets = Posts = Maximum = 0;
            Status = null;
            Level = null;
            ReplayedSignatures = [];
            ResponseIdentityMatched = false;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.Method == HttpMethod.Get) { Gets++; }
            if (request.Method == HttpMethod.Post) {
                Posts++;
                using JsonDocument doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                JsonElement config = doc.RootElement.GetProperty("generationConfig");
                Maximum = config.GetProperty("maxOutputTokens").GetInt32();
                Level = config.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString();
                Assert.False(config.GetProperty("thinkingConfig").TryGetProperty("includeThoughts", out _));
                JsonElement[] parts = doc.RootElement.GetProperty("contents").EnumerateArray()
                    .SelectMany(c => c.GetProperty("parts").EnumerateArray()).ToArray();
                ReplayedSignatures = parts.Where(p => p.TryGetProperty("thoughtSignature", out _))
                    .Select(p => p.GetProperty("thoughtSignature").GetString()!).ToArray();
                JsonElement[] calls = parts.Where(p => p.TryGetProperty("functionCall", out _))
                    .Select(p => p.GetProperty("functionCall")).ToArray();
                JsonElement[] responses = parts.Where(p => p.TryGetProperty("functionResponse", out _))
                    .Select(p => p.GetProperty("functionResponse")).ToArray();
                ResponseIdentityMatched = calls.Length == 1 && responses.Length == 1
                    && calls[0].GetProperty("id").GetString() == responses[0].GetProperty("id").GetString()
                    && calls[0].GetProperty("name").GetString() == responses[0].GetProperty("name").GetString();
            }
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            Status = response.StatusCode;
            return response;
        }
    }
}
