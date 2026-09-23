using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Completion.Anthropic.Tests;

/// <summary>
/// Opt-in basic text and same-model continuation through an operator-selected
/// Anthropic Messages endpoint. Each run makes two live generations.
/// </summary>
public sealed class AnthropicOpus55BasicLiveTests(ITestOutputHelper output) {
    private const string EnableEnv = "ATELIA_RUN_ANTHROPIC_OPUS55_BASIC_LIVE";
    private const string BaseUrlEnv = "CLAUDE_BASE_URL";
    private const string ApiKeyEnv = "CLAUDE_API_KEY";
    private const string RawLogDirectoryEnv = "ATELIA_ANTHROPIC_OPUS55_RAW_LOG_DIR";
    private const string Model = "claude-opus-5-5";
    private const string Marker = "ATELIA55";

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_BasicTextAndSameModelContinuation() {
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
                $"anthropic-opus55-basic-{Guid.NewGuid():N}.jsonl");
            handler = new CompletionHttpClientBuilder()
                .UsePrimaryHandler(statusProbe)
                .AddJsonLinesGoldenLogSink(rawLogPath)
                .BuildHandler();
        }
        using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
            baseAddress, handler);
        var client = new AnthropicClient(apiKey, httpClient);

        var firstUser = new ObservationMessage(
            $"Reply with exactly the uppercase marker {Marker}.");
        CompletionResult first = await CallAndCheckAsync(
            "first", CreateRequest([firstUser]), client, statusProbe);

        var secondUser = new ObservationMessage(
            "Which marker did I ask you to reply with in the previous turn? Reply with only that uppercase marker.");
        await CallAndCheckAsync("continuation",
            CreateRequest([firstUser, first.Message, secondUser]),
            client, statusProbe);

        if (rawLogPath is not null) {
            Assert.True(File.Exists(rawLogPath), "The requested raw exchange log was not created.");
            output.WriteLine($"rawLog={rawLogPath}");
        }
    }

    private static CompletionRequest CreateRequest(
        IReadOnlyList<IHistoryMessage> messages
    ) => new(
        Model,
        new CompletionPromptPrefix(
            "Answer briefly. Follow the user's output format instruction.",
            CompletionOutputContract.ProviderDefault([]),
            messages),
        tailMessages: []);

    private async Task<CompletionResult> CallAndCheckAsync(
        string stage,
        CompletionRequest request,
        AnthropicClient client,
        StatusProbeHandler statusProbe
    ) {
        statusProbe.Reset();
        var observer = new CompletionStreamObserver();
        int textDeltaCount = 0;
        observer.ReceivedTextDelta += _ => textDeltaCount++;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
                + $"modelsHttp={statusProbe.ModelsStatusCode?.ToString() ?? "none"} messagesHttp={statusProbe.MessagesStatusCode} "
                + $"exception={exception.GetType().Name}{failure}");
            Assert.Fail($"Live Anthropic invocation failed: model={request.ModelId} stage={stage} "
                + $"modelsHttp={statusProbe.ModelsStatusCode?.ToString() ?? "none"} messagesHttp={statusProbe.MessagesStatusCode} "
                + $"exception={exception.GetType().Name}{failure}");
            throw;
        }

        string text = result.Message.GetFlattenedText().Trim();
        bool markerPresent = text.Contains(Marker, StringComparison.OrdinalIgnoreCase);
        output.WriteLine($"model={request.ModelId} stage={stage} elapsedMs={timer.ElapsedMilliseconds} "
            + $"modelsHttp={statusProbe.ModelsStatusCode?.ToString() ?? "none"} messagesHttp={statusProbe.MessagesStatusCode} "
            + $"termination={result.Termination.Kind} textLength={text.Length} "
            + $"textDeltas={textDeltaCount} markerPresent={markerPresent} "
            + $"failureKind={result.Failure?.Kind} providerCode={result.Failure?.ProviderCode}");

        Assert.Equal(200, statusProbe.MessagesStatusCode);
        Assert.Null(statusProbe.ModelsStatusCode);
        Assert.Equal(request.ModelId, result.Invocation.Model);
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.True(textDeltaCount > 0, "No streamed text delta reached the observer.");
        Assert.True(markerPresent, "The completed text did not contain the requested marker.");
        return result;
    }

    private sealed class StatusProbeHandler(HttpMessageHandler innerHandler)
        : DelegatingHandler(innerHandler) {
        public int? ModelsStatusCode { get; private set; }
        public int? MessagesStatusCode { get; private set; }

        public void Reset() {
            ModelsStatusCode = null;
            MessagesStatusCode = null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith($"/v1/models/{Model}", StringComparison.Ordinal)) {
                ModelsStatusCode = (int)response.StatusCode;
            }
            else if (path.EndsWith("/v1/messages", StringComparison.Ordinal)) {
                MessagesStatusCode = (int)response.StatusCode;
            }
            return response;
        }
    }
}
