using System.Net;
using System.Net.Http;

namespace Atelia.Completion;

internal static class CompletionHttpRequestUtility {
    private const int MaxErrorBodyLength = 512;
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
            var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode) { return response; }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = response.StatusCode;
            response.Dispose();
            throw CreateRequestFailure(requestDisplayName, statusCode, errorBody);
        }
    }

    public static HttpRequestException CreateRequestFailure(string requestDisplayName, HttpStatusCode statusCode, string errorBody) {
        if (string.IsNullOrWhiteSpace(requestDisplayName)) { throw new ArgumentException("Request display name must not be blank.", nameof(requestDisplayName)); }

        var normalizedBody = NormalizeSingleLine(errorBody);
        normalizedBody = Truncate(normalizedBody, MaxErrorBodyLength);

        return new HttpRequestException(
            $"{requestDisplayName} failed status={(int)statusCode} body={normalizedBody}",
            inner: null,
            statusCode: statusCode
        );
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
