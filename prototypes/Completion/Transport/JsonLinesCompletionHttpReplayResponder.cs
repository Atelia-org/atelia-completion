using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Atelia.Completion.Transport;

/// <summary>
/// 从 JSON Lines golden log 顺序读取并回放 HTTP 响应。
/// </summary>
/// <remarks>
/// <para>
/// 当前是严格的顺序 replay：每次请求都会消耗下一条 exchange，并校验 method / uri / requestText。
/// </para>
/// <para>
/// 这使得它更像 deterministic test fixture，而不是通用匹配型 mock server。
/// </para>
/// </remarks>
public sealed class JsonLinesCompletionHttpReplayResponder : ICompletionHttpReplayResponder {
    private static readonly JsonSerializerOptions SerializerOptions = new() {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly Queue<CompletionHttpExchange> _remainingExchanges;
    private readonly string _responseMediaType;

    public JsonLinesCompletionHttpReplayResponder(string filePath, string responseMediaType = "text/event-stream") {
        if (string.IsNullOrWhiteSpace(filePath)) {
            throw new ArgumentException("File path must not be blank.", nameof(filePath));
        }

        if (string.IsNullOrWhiteSpace(responseMediaType)) {
            throw new ArgumentException("Response media type must not be blank.", nameof(responseMediaType));
        }

        if (!File.Exists(filePath)) {
            throw new FileNotFoundException("Replay golden log file does not exist.", filePath);
        }

        _remainingExchanges = new Queue<CompletionHttpExchange>(ReadExchanges(filePath));
        _responseMediaType = responseMediaType;
    }

    public HttpResponseMessage CreateResponse(CompletionHttpReplayRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        CompletionHttpExchange expected;
        lock (_gate) {
            if (_remainingExchanges.Count == 0) {
                throw new InvalidOperationException(
                    $"Replay log is exhausted. No exchange remains for {request.Method} {request.RequestUri ?? "<null>"}."
                );
            }

            expected = _remainingExchanges.Peek();
            ValidateRequest(request, expected);
            _remainingExchanges.Dequeue();
        }

        if (!string.IsNullOrWhiteSpace(expected.ErrorText)) {
            throw new HttpRequestException(
                $"Replayed transport failure for {request.Method} {request.RequestUri ?? "<null>"}: {expected.ErrorText}"
            );
        }

        var statusCode = expected.StatusCode is int code
            ? (HttpStatusCode)code
            : HttpStatusCode.OK;

        var response = new HttpResponseMessage(statusCode);
        if (expected.ResponseText is not null) {
            response.Content = new StringContent(expected.ResponseText, Encoding.UTF8, _responseMediaType);
        }

        return response;
    }

    private static IEnumerable<CompletionHttpExchange> ReadExchanges(string filePath) {
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(filePath)) {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(rawLine)) {
                continue;
            }

            CompletionHttpExchange? exchange;
            try {
                exchange = JsonSerializer.Deserialize<CompletionHttpExchange>(rawLine, SerializerOptions);
            }
            catch (JsonException ex) {
                throw new InvalidOperationException($"Failed to parse replay golden log line {lineNumber}: {ex.Message}", ex);
            }

            if (exchange is null) {
                throw new InvalidOperationException($"Replay golden log line {lineNumber} deserialized to null.");
            }

            yield return exchange;
        }
    }

    private static void ValidateRequest(CompletionHttpReplayRequest actual, CompletionHttpExchange expected) {
        if (!string.Equals(actual.Method, expected.Method, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"Replay request method mismatch. Expected {expected.Method}, got {actual.Method}."
            );
        }

        if (!string.Equals(actual.RequestUri, expected.RequestUri, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"Replay request uri mismatch. Expected {expected.RequestUri ?? "<null>"}, got {actual.RequestUri ?? "<null>"}."
            );
        }

        if (!string.Equals(actual.RequestText, expected.RequestText, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                "Replay request text mismatch. The actual request no longer matches the recorded golden log entry."
            );
        }
    }
}
