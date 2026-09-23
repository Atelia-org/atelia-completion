using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Completion.Gemini.Tests;

/// <summary>
/// Opt-in, real Google API experiment using production GeminiClient for both turns.
/// Captures only in-memory request metadata; no credentials or signed payloads are logged.
/// </summary>
public sealed class GeminiCrossModelThinkingLiveTests(ITestOutputHelper output) {
    private const string EnableEnv = "ATELIA_RUN_GEMINI_31_LITE_TO_38_FLASH_LIVE";
    private const string SourceModel = "gemini-3.1-flash-lite";
    private const string TargetModel = "gemini-3.8-flash";

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_FlashLiteThinkingSignatureIsReplayedIntoFlash() {
        if (Environment.GetEnvironmentVariable(EnableEnv) != "1") { return; }

        string apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? throw new InvalidOperationException("GEMINI_API_KEY must be set.");
        if (string.IsNullOrWhiteSpace(apiKey)) {
            throw new InvalidOperationException("GEMINI_API_KEY must not be blank.");
        }

        using var probe = new RequestProbeHandler(new HttpClientHandler());
        using var http = CompletionHttpTransportFactory.CreateLiveClient(
            new Uri("https://generativelanguage.googleapis.com/"), probe);
        var client = new GeminiClient(apiKey, http);

        var firstUser = new ObservationMessage(
            "Calculate 17 times 19. Reply with just the decimal product.");
        CompletionResult first = await CallAsync(
            client, CreateRequest(SourceModel, [firstUser]), probe, "seed");
        Assert.Equal("323", first.Message.GetFlattenedText().Trim());
        var replay = Assert.Single(first.Message.Blocks.OfType<GeminiReplayBlock>());
        Assert.Equal(SourceModel, replay.Origin.Model);
        string[] signatures = ReadSignatures(replay.OpaquePayload.Span);
        Assert.NotEmpty(signatures);

        var secondUser = new ObservationMessage(
            "What product did you give in the previous turn? Reply with digits only.");
        CompletionResult second = await CallAsync(
            client,
            CreateRequest(TargetModel, [firstUser, first.Message, secondUser]),
            probe,
            "continuation");

        Assert.Equal(signatures, probe.ReplayedSignatures);
        Assert.Equal(TargetModel, second.Invocation.Model);
        Assert.Contains("323", second.Message.GetFlattenedText(), StringComparison.Ordinal);
        output.WriteLine($"source={SourceModel} target={TargetModel} "
            + $"seedSignatureCount={signatures.Length} replayedSignatureCount={probe.ReplayedSignatures.Length} "
            + "crossModelCompleted=true markerPresent=true");
    }

    private async Task<CompletionResult> CallAsync(
        GeminiClient client,
        CompletionRequest request,
        RequestProbeHandler probe,
        string stage
    ) {
        probe.Reset();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var timer = Stopwatch.StartNew();
        CompletionResult result;
        try {
            result = await client.StreamCompletionAsync(request, observer: null, deadline.Token);
        }
        catch (Exception exception) {
            output.WriteLine($"stage={stage} model={request.ModelId} "
                + $"modelHttp={probe.ModelStatusCode?.ToString() ?? "none"} "
                + $"completionHttp={probe.CompletionStatusCode?.ToString() ?? "none"} "
                + $"exception={exception.GetType().Name}");
            throw;
        }

        output.WriteLine($"stage={stage} model={request.ModelId} elapsedMs={timer.ElapsedMilliseconds} "
            + $"modelHttp={probe.ModelStatusCode?.ToString() ?? "cached"} "
            + $"completionHttp={probe.CompletionStatusCode?.ToString() ?? "none"} "
            + $"termination={result.Termination.Kind} textLength={result.Message.GetFlattenedText().Length}");
        Assert.Equal(HttpStatusCode.OK, probe.CompletionStatusCode);
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        return result;
    }

    private static CompletionRequest CreateRequest(
        string model,
        IReadOnlyList<IHistoryMessage> messages
    ) => new(model,
        new CompletionPromptPrefix(
            "Answer briefly and follow the user's requested format.",
            CompletionOutputContract.ProviderDefault([]),
            messages),
        tailMessages: []);

    private static string[] ReadSignatures(ReadOnlySpan<byte> payload) {
        using JsonDocument doc = JsonDocument.Parse(payload.ToArray());
        return doc.RootElement.GetProperty("parts").EnumerateArray()
            .Where(static part => part.TryGetProperty("thoughtSignature", out _))
            .Select(static part => part.GetProperty("thoughtSignature").GetString()!)
            .ToArray();
    }

    private sealed class RequestProbeHandler(HttpMessageHandler inner) : DelegatingHandler(inner) {
        public HttpStatusCode? ModelStatusCode { get; private set; }
        public HttpStatusCode? CompletionStatusCode { get; private set; }
        public string[] ReplayedSignatures { get; private set; } = [];

        public void Reset() {
            ModelStatusCode = null;
            CompletionStatusCode = null;
            ReplayedSignatures = [];
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Post
                && path.EndsWith(":streamGenerateContent", StringComparison.Ordinal)) {
                if (path.Contains(TargetModel, StringComparison.Ordinal)) {
                    string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using JsonDocument doc = JsonDocument.Parse(body);
                    ReplayedSignatures = doc.RootElement.GetProperty("contents")
                        .EnumerateArray()
                        .Where(static content => content.GetProperty("role").GetString() == "model")
                        .SelectMany(static content => content.GetProperty("parts").EnumerateArray())
                        .Where(static part => part.TryGetProperty("thoughtSignature", out _))
                        .Select(static part => part.GetProperty("thoughtSignature").GetString()!)
                        .ToArray();
                }
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
                CompletionStatusCode = response.StatusCode;
                return response;
            }

            HttpResponseMessage modelResponse = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Get && path.StartsWith("/v1beta/models/", StringComparison.Ordinal)) {
                ModelStatusCode = modelResponse.StatusCode;
            }
            return modelResponse;
        }
    }
}
