using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Completion.OpenAI.Tests;

/// <summary>
/// Opt-in acceptance of ordinary text generation and same-model conversation
/// continuation through the ChatGPT Codex subscription route. Each case makes
/// two live generations. No tools or GPT-6 mid-turn controls are used.
/// </summary>
public sealed class OpenAICodexGpt6BasicLiveTests(ITestOutputHelper output) {
    private const string EnableEnv = "ATELIA_RUN_CODEX_GPT6_BASIC_LIVE";
    private const string AuthFileEnv = "ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE";
    private const string RawLogDirectoryEnv = "ATELIA_CODEX_GPT6_BASIC_RAW_LOG_DIR";
    private const string Marker = "ATELIA6";

    [Theory]
    [InlineData("gpt-6-sol")]
    [InlineData("gpt-6-luna")]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_BasicTextAndSameModelContinuation(string model) {
        if (Environment.GetEnvironmentVariable(EnableEnv) != "1") { return; }

        string authFile = Environment.GetEnvironmentVariable(AuthFileEnv)
            ?? throw new InvalidOperationException($"{AuthFileEnv} must name an absolute auth.json path.");
        if (!Path.IsPathFullyQualified(authFile)) {
            throw new InvalidOperationException($"{AuthFileEnv} must name an absolute auth.json path.");
        }

        string? rawDirectory = Environment.GetEnvironmentVariable(RawLogDirectoryEnv);
        if (rawDirectory is not null && (!OperatingSystem.IsLinux()
            || !Path.IsPathFullyQualified(rawDirectory)
            || !Directory.Exists(rawDirectory))) {
            throw new InvalidOperationException(
                $"{RawLogDirectoryEnv} requires an existing absolute directory on Linux.");
        }

        var provider = new CodexCliAuthFileCredentialProvider(authFile);
        using var credentialDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CodexSubscriptionCredential credential =
            await provider.GetCredentialAsync(credentialDeadline.Token);

        var statusProbe = new StatusProbeHandler(
            OpenAICodexResponsesClient.CreateProductionHandler());
        HttpMessageHandler handler = statusProbe;
        string? rawLogPath = null;
        if (rawDirectory is not null) {
            rawLogPath = Path.Combine(rawDirectory,
                $"gpt6-basic-{model}-{Guid.NewGuid():N}.jsonl");
            handler = new CompletionHttpClientBuilder()
                .UsePrimaryHandler(statusProbe)
                .AddJsonLinesGoldenLogSink(rawLogPath)
                .BuildHandler();
        }

        using var client = new OpenAICodexResponsesClient(
            provider,
            new OpenAICodexResponsesClientOptions {
                ExpectedAccountFingerprint = credential.AccountFingerprint,
                Originator = "atelia-gpt6-basic-live",
                ProductName = "Atelia",
                ProductVersion = "gpt6-basic-live-v1",
                MaxConcurrentRequests = 1
            },
            handler);

        var firstUser = new ObservationMessage(
            $"Reply with exactly the uppercase marker {Marker}.");
        var firstRequest = CreateRequest(model, [firstUser]);
        CompletionResult first = await CallAndCheckAsync(
            "first", firstRequest, client, statusProbe);

        var secondUser = new ObservationMessage(
            "Which marker did I ask you to reply with in the previous turn? Reply with only that uppercase marker.");
        var secondRequest = CreateRequest(model,
            [firstUser, first.Message, secondUser]);
        await CallAndCheckAsync("continuation", secondRequest, client, statusProbe);

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
        OpenAICodexResponsesClient client,
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
                : exception is OpenAICodexResponsesException codex
                    ? $" reason={codex.Reason}"
                    : string.Empty;
            output.WriteLine($"model={request.ModelId} stage={stage} elapsedMs={timer.ElapsedMilliseconds} "
                + $"http={statusProbe.LastStatusCode} exception={exception.GetType().Name}{failure}");
            Assert.Fail($"Live Codex invocation failed: model={request.ModelId} stage={stage} "
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
