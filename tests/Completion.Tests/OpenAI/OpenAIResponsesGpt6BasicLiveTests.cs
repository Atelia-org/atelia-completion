using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Completion.OpenAI.Tests;

/// <summary>
/// Opt-in acceptance of ordinary text generation and same-model continuation
/// through the public Responses adapter and an operator-selected API relay.
/// Each case makes two live generations. No tools or GPT-6 mid-turn controls
/// are used.
/// </summary>
public sealed class OpenAIResponsesGpt6BasicLiveTests(ITestOutputHelper output) {
    private const string EnableEnv = "ATELIA_RUN_OPENAI_RESPONSES_GPT6_BASIC_LIVE";
    private const string BaseUrlEnv = "GPT_BASE_URL";
    private const string ApiKeyEnv = "GPT_API_KEY";
    private const string RawLogDirectoryEnv = "ATELIA_OPENAI_RESPONSES_GPT6_RAW_LOG_DIR";
    private const string Marker = "ATELIA6";

    [Theory]
    [InlineData("gpt-6-sol")]
    [InlineData("gpt-6-luna")]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_BasicTextAndSameModelContinuation(string model) {
        if (Environment.GetEnvironmentVariable(EnableEnv) != "1") { return; }

        string baseUrl = Environment.GetEnvironmentVariable(BaseUrlEnv)
            ?? throw new InvalidOperationException($"{BaseUrlEnv} must name an absolute API base URL.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseAddress)
            || baseAddress.Scheme != Uri.UriSchemeHttps
            || baseAddress.UserInfo.Length != 0
            || baseAddress.Query.Length != 0
            || baseAddress.Fragment.Length != 0) {
            throw new InvalidOperationException(
                $"{BaseUrlEnv} must name an HTTPS API base URL without user info, query, or fragment.");
        }
        string apiKey = Environment.GetEnvironmentVariable(ApiKeyEnv)
            ?? throw new InvalidOperationException($"{ApiKeyEnv} must be set.");
        if (string.IsNullOrWhiteSpace(apiKey)) {
            throw new InvalidOperationException($"{ApiKeyEnv} must not be blank.");
        }

        string? rawDirectory = Environment.GetEnvironmentVariable(RawLogDirectoryEnv);
        if (rawDirectory is not null && (!OperatingSystem.IsLinux()
            || !Path.IsPathFullyQualified(rawDirectory)
            || !Directory.Exists(rawDirectory))) {
            throw new InvalidOperationException(
                $"{RawLogDirectoryEnv} requires an existing absolute directory on Linux.");
        }

        var statusProbe = new StatusProbeHandler(new HttpClientHandler());
        HttpMessageHandler handler = statusProbe;
        string? rawLogPath = null;
        if (rawDirectory is not null) {
            rawLogPath = Path.Combine(rawDirectory,
                $"responses-gpt6-basic-{model}-{Guid.NewGuid():N}.jsonl");
            handler = new CompletionHttpClientBuilder()
                .UsePrimaryHandler(statusProbe)
                .AddJsonLinesGoldenLogSink(rawLogPath)
                .BuildHandler();
        }
        using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
            baseAddress, handler);
        var client = new OpenAIResponsesClient(apiKey, httpClient);

        var firstUser = new ObservationMessage(
            $"Reply with exactly the uppercase marker {Marker}.");
        CompletionResult first = await CallAndCheckAsync(
            "first", CreateRequest(model, [firstUser]), client, statusProbe);

        var secondUser = new ObservationMessage(
            "Which marker did I ask you to reply with in the previous turn? Reply with only that uppercase marker.");
        await CallAndCheckAsync("continuation",
            CreateRequest(model, [firstUser, first.Message, secondUser]),
            client, statusProbe);

        if (rawLogPath is not null) {
            Assert.True(File.Exists(rawLogPath), "The requested raw exchange log was not created.");
            output.WriteLine($"rawLog={rawLogPath}");
        }
    }

    private static CompletionRequest CreateRequest(
        string model,
        IReadOnlyList<IHistoryMessage> messages
    ) => new(
        model,
        new CompletionPromptPrefix(
            "Answer briefly. Follow the user's output format instruction.",
            CompletionOutputContract.ProviderDefault([]),
            messages),
        tailMessages: []);

    private async Task<CompletionResult> CallAndCheckAsync(
        string stage,
        CompletionRequest request,
        OpenAIResponsesClient client,
        StatusProbeHandler statusProbe
    ) {
        statusProbe.Reset();
        var observer = new CompletionStreamObserver();
        int textDeltaCount = 0;
        observer.ReceivedTextDelta += _ => textDeltaCount++;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var timer = Stopwatch.StartNew();
        CompletionResult result;
        try {
            result = await client.StreamCompletionAsync(
                request, observer, deadline.Token);
        }
        catch (Exception exception) {
            string failure = exception is CompletionFailureException classified
                ? $" kind={classified.Failure.Kind} http={classified.Failure.HttpStatusCode} code={classified.Failure.ProviderCode}"
                : string.Empty;
            output.WriteLine($"model={request.ModelId} stage={stage} elapsedMs={timer.ElapsedMilliseconds} "
                + $"http={statusProbe.LastStatusCode} exception={exception.GetType().Name}{failure}");
            Assert.Fail($"Live Responses invocation failed: model={request.ModelId} stage={stage} "
                + $"http={statusProbe.LastStatusCode} exception={exception.GetType().Name}{failure}");
            throw;
        }

        string text = result.Message.GetFlattenedText().Trim();
        bool markerPresent = text.Contains(Marker, StringComparison.OrdinalIgnoreCase);
        output.WriteLine($"model={request.ModelId} stage={stage} elapsedMs={timer.ElapsedMilliseconds} "
            + $"http={statusProbe.LastStatusCode} termination={result.Termination.Kind} "
            + $"textLength={text.Length} textDeltas={textDeltaCount} markerPresent={markerPresent} "
            + $"failureKind={result.Failure?.Kind} providerCode={result.Failure?.ProviderCode}");

        Assert.Equal(200, statusProbe.LastStatusCode);
        Assert.Equal(request.ModelId, result.Invocation.Model);
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.True(textDeltaCount > 0, "No streamed text delta reached the observer.");
        Assert.True(markerPresent, "The completed text did not contain the requested marker.");
        return result;
    }

    private sealed class StatusProbeHandler(HttpMessageHandler innerHandler)
        : DelegatingHandler(innerHandler) {
        public int? LastStatusCode { get; private set; }

        public void Reset() => LastStatusCode = null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            LastStatusCode = (int)response.StatusCode;
            return response;
        }
    }
}
