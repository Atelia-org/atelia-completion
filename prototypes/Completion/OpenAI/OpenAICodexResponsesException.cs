using System.Net;

namespace Atelia.Completion.OpenAI;

public enum OpenAICodexResponsesFailureReason {
    CodexReauthenticationRequired,
    CodexAccessDenied,
    CodexRateLimited,
    UnexpectedBackendRedirect,
    TransportOutcomeUnknown,
    BackendFailure,
    ProtocolCompatibilityFailure,
}

public sealed class OpenAICodexResponsesException : Exception {
    internal OpenAICodexResponsesException(
        OpenAICodexResponsesFailureReason reason,
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null
    ) : base(message) {
        Reason = reason;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public OpenAICodexResponsesFailureReason Reason { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

}
