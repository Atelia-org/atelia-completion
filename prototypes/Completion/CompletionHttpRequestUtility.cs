using System.Net;
using System.Net.Http;

namespace Atelia.Completion;

internal static class CompletionHttpRequestUtility {
    private const int MaxErrorBodyLength = 512;
    private const int MaxTransportFailureSummaryLength = 512;

    public static async Task<HttpResponseMessage> SendStreamingRequestAsync(
        HttpClient httpClient,
        HttpRequestMessage httpRequest,
        string requestDisplayName,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(httpRequest);
        if (string.IsNullOrWhiteSpace(requestDisplayName)) {
            throw new ArgumentException("Request display name must not be blank.", nameof(requestDisplayName));
        }

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
        if (string.IsNullOrWhiteSpace(requestDisplayName)) {
            throw new ArgumentException("Request display name must not be blank.", nameof(requestDisplayName));
        }

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
        if (string.IsNullOrWhiteSpace(text)) {
            return "<empty>";
        }

        return text.ReplaceLineEndings(" ").Trim();
    }

    private static string Truncate(string text, int maxLength) {
        if (text.Length <= maxLength) {
            return text;
        }

        return text[..maxLength];
    }
}
