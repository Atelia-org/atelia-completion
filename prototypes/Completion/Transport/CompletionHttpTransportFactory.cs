using System.Net.Http;

namespace Atelia.Completion.Transport;

/// <summary>
/// Completion HTTP transport 的高层装配入口。
/// </summary>
public static class CompletionHttpTransportFactory {
    public static HttpClient CreateLiveClient(Uri baseAddress, HttpMessageHandler? primaryHandler = null) {
        return CreateLiveSetup(baseAddress, primaryHandler).HttpClient;
    }

    public static CompletionHttpTransportSetup CreateLiveSetup(Uri baseAddress, HttpMessageHandler? primaryHandler = null) {
        var builder = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(primaryHandler ?? new HttpClientHandler());

        var httpClient = BuildConfiguredHttpClient(builder, baseAddress);
        return new CompletionHttpTransportSetup(httpClient, CompletionHttpTransportMode.Live, ArtifactPath: null);
    }

    public static HttpClient CreateJsonLinesReplayClient(
        Uri baseAddress,
        string replayLogPath,
        string responseMediaType = "text/event-stream"
    ) {
        return CreateJsonLinesReplaySetup(baseAddress, replayLogPath, responseMediaType).HttpClient;
    }

    public static CompletionHttpTransportSetup CreateJsonLinesReplaySetup(
        Uri baseAddress,
        string replayLogPath,
        string responseMediaType = "text/event-stream"
    ) {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (string.IsNullOrWhiteSpace(replayLogPath)) { throw new ArgumentException("Replay log path must not be blank.", nameof(replayLogPath)); }

        var httpClient = BuildConfiguredHttpClient(
            new CompletionHttpClientBuilder()
                .UseJsonLinesReplayResponder(replayLogPath, responseMediaType),
            baseAddress
        );

        return new CompletionHttpTransportSetup(httpClient, CompletionHttpTransportMode.Replay, replayLogPath);
    }

    public static CompletionHttpTransportSetup CreateFromPaths(
        Uri baseAddress,
        string? recordLogPath,
        string? replayLogPath,
        string replayResponseMediaType = "text/event-stream"
    ) {
        ArgumentNullException.ThrowIfNull(baseAddress);

        if (!string.IsNullOrWhiteSpace(recordLogPath) && !string.IsNullOrWhiteSpace(replayLogPath)) { throw new InvalidOperationException("Record log path and replay log path cannot both be set at the same time."); }

        if (!string.IsNullOrWhiteSpace(replayLogPath)) { return CreateJsonLinesReplaySetup(baseAddress, replayLogPath, replayResponseMediaType); }

        if (!string.IsNullOrWhiteSpace(recordLogPath)) {
            var builder = new CompletionHttpClientBuilder()
                .AddJsonLinesGoldenLogSink(recordLogPath)
                .UsePrimaryHandler(new HttpClientHandler());
            var httpClient = BuildConfiguredHttpClient(builder, baseAddress);
            return new CompletionHttpTransportSetup(httpClient, CompletionHttpTransportMode.Record, recordLogPath);
        }

        return CreateLiveSetup(baseAddress);
    }

    public static CompletionHttpTransportSetup CreateFromEnvironmentVariables(
        Uri baseAddress,
        string recordLogPathEnvVar = "ATELIA_COMPLETION_GOLDEN_LOG",
        string replayLogPathEnvVar = "ATELIA_COMPLETION_REPLAY_LOG",
        string replayResponseMediaType = "text/event-stream"
    ) {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (string.IsNullOrWhiteSpace(recordLogPathEnvVar)) { throw new ArgumentException("Record log env var name must not be blank.", nameof(recordLogPathEnvVar)); }

        if (string.IsNullOrWhiteSpace(replayLogPathEnvVar)) { throw new ArgumentException("Replay log env var name must not be blank.", nameof(replayLogPathEnvVar)); }

        var recordLogPath = Environment.GetEnvironmentVariable(recordLogPathEnvVar);
        var replayLogPath = Environment.GetEnvironmentVariable(replayLogPathEnvVar);
        return CreateFromPaths(baseAddress, recordLogPath, replayLogPath, replayResponseMediaType);
    }

    private static HttpClient BuildConfiguredHttpClient(CompletionHttpClientBuilder builder, Uri baseAddress) {
        ArgumentNullException.ThrowIfNull(builder);

        var httpClient = builder.Build();
        httpClient.BaseAddress = CompletionHttpRequestUtility.NormalizeBaseAddress(baseAddress);
        return httpClient;
    }
}

public enum CompletionHttpTransportMode {
    Live,
    Record,
    Replay,
}

public sealed record CompletionHttpTransportSetup(
    HttpClient HttpClient,
    CompletionHttpTransportMode Mode,
    string? ArtifactPath
) {
    public string Describe() {
        return Mode switch {
            CompletionHttpTransportMode.Live => $"transport: live network ({HttpClient.BaseAddress})",
            CompletionHttpTransportMode.Record => $"transport: record -> {ArtifactPath} ({HttpClient.BaseAddress})",
            CompletionHttpTransportMode.Replay => $"transport: replay <- {ArtifactPath} ({HttpClient.BaseAddress})",
            _ => throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Unknown transport mode."),
        };
    }
}
