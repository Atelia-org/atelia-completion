using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion;

internal static class CompletionHttpRequestUtility {
    private const int MaxTransportFailureSummaryLength = 512;

    public static Uri NormalizeBaseAddress(Uri baseAddress) {
        ArgumentNullException.ThrowIfNull(baseAddress);
        EnsureAbsoluteBaseAddress(baseAddress, nameof(baseAddress));

        if (!string.IsNullOrEmpty(baseAddress.Query)) { throw new ArgumentException("Base address must not contain a query string.", nameof(baseAddress)); }

        if (!string.IsNullOrEmpty(baseAddress.Fragment)) { throw new ArgumentException("Base address must not contain a fragment.", nameof(baseAddress)); }

        var builder = new UriBuilder(baseAddress);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal)) {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    public static Uri RequireConfiguredBaseAddress(HttpClient httpClient, string clientName) {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (string.IsNullOrWhiteSpace(clientName)) { throw new ArgumentException("Client name must not be blank.", nameof(clientName)); }

        var baseAddress = httpClient.BaseAddress ?? throw new InvalidOperationException(
            $"{clientName} requires HttpClient.BaseAddress to be configured by the caller."
        );

        EnsureAbsoluteBaseAddress(baseAddress, $"{clientName} HttpClient.BaseAddress");

        if (!string.IsNullOrEmpty(baseAddress.Query)) { throw new InvalidOperationException($"{clientName} requires HttpClient.BaseAddress to omit any query string."); }

        if (!string.IsNullOrEmpty(baseAddress.Fragment)) { throw new InvalidOperationException($"{clientName} requires HttpClient.BaseAddress to omit any fragment."); }

        if (!baseAddress.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)) { throw new InvalidOperationException($"{clientName} requires HttpClient.BaseAddress to end with '/'."); }

        return baseAddress;
    }

    public static async Task<HttpResponseMessage> SendStreamingRequestAsync(
        HttpClient httpClient,
        HttpRequestMessage httpRequest,
        string requestDisplayName,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(httpRequest);
        if (string.IsNullOrWhiteSpace(requestDisplayName)) { throw new ArgumentException("Request display name must not be blank.", nameof(requestDisplayName)); }

        using (httpRequest) {
            var response = await SendAsync(httpClient, httpRequest, cancellationToken);
            if (response.IsSuccessStatusCode) {
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) {
                    return response;
                }

                response.Dispose();
                throw new InvalidDataException(
                    $"{requestDisplayName} expected Content-Type 'text/event-stream' "
                    + $"but received '{mediaType ?? "<missing>"}'."
                );
            }

            using (response) {
                throw await CreateHttpFailureAsync(response, requestDisplayName, cancellationToken);
            }
        }
    }

    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken
    ) {
        try {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException) {
            throw TransportFailure(exception);
        }
    }

    internal static async Task<Stream> OpenStreamAsync(HttpContent content, CancellationToken cancellationToken) {
        try {
            return await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException) {
            throw TransportFailure(exception);
        }
    }

    internal static CompletionFailureException TransportFailure(Exception exception) =>
        new(new(CompletionFailureKind.Transport), "Completion transport I/O failed.", exception);

    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response, DateTimeOffset? now = null) {
        var header = response.Headers.RetryAfter;
        TimeSpan? delay = header?.Delta ?? (header?.Date - (now ?? DateTimeOffset.UtcNow));
        return delay is { Ticks: >= 0 } ? delay : null;
    }

    internal static string? ReadProviderCode(JsonNode? envelope) {
        if (envelope is not JsonObject obj) { return null; }
        JsonObject error = obj["error"] as JsonObject ?? obj;
        foreach (string field in new[] { "code", "status", "type" }) {
            if (error[field] is JsonValue value && value.TryGetValue<string>(out string? code)
                && !string.IsNullOrWhiteSpace(code)) { return code; }
        }
        return null;
    }

    internal static async Task<CompletionFailureException> CreateHttpFailureAsync(
        HttpResponseMessage response, string displayName, CancellationToken cancellationToken,
        bool includeDiagnosticBody = true
    ) {
        string? code = null;
        string? body = null;
        Exception? readFailure = null;
        try {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try { code = ReadProviderCode(JsonNode.Parse(body)); }
            catch (JsonException) { /* Non-JSON bodies do not erase authoritative HTTP status. */ }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException) {
            readFailure = exception;
        }
        return new CompletionFailureException(
            new(CompletionFailureKind.Http, (int)response.StatusCode, code, ReadRetryAfter(response)),
            $"{displayName} failed with HTTP status {(int)response.StatusCode}."
                + (includeDiagnosticBody ? $" Response body: {body}" : string.Empty),
            includeDiagnosticBody ? readFailure : null);
    }

    public static string FormatTransportFailureSummary(Exception exception) {
        ArgumentNullException.ThrowIfNull(exception);

        var typeName = exception.GetType().FullName ?? exception.GetType().Name;
        var message = NormalizeSingleLine(exception.Message);
        var summary = $"{typeName}: {message}";

        if (exception.InnerException is not null) {
            var innerTypeName = exception.InnerException.GetType().FullName ?? exception.InnerException.GetType().Name;
            var innerMessage = NormalizeSingleLine(exception.InnerException.Message);
            summary += $" | inner: {innerTypeName}: {innerMessage}";
        }

        return Truncate(summary, MaxTransportFailureSummaryLength);
    }

    private static string NormalizeSingleLine(string? text) {
        if (string.IsNullOrWhiteSpace(text)) { return "<empty>"; }

        return text.ReplaceLineEndings(" ").Trim();
    }

    private static string Truncate(string text, int maxLength) {
        if (text.Length <= maxLength) { return text; }

        return text[..maxLength];
    }

    private static void EnsureAbsoluteBaseAddress(Uri baseAddress, string paramName) {
        if (!baseAddress.IsAbsoluteUri) { throw new ArgumentException("Base address must be an absolute URI.", paramName); }
    }
}
