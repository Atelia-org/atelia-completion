using System.Net;

namespace Atelia.Completion.OpenAI;

public enum OpenAICodexResponsesFailureReason {
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
        TimeSpan? retryAfter = null,
        string? providerErrorCode = null,
        string? providerErrorType = null,
        string? providerErrorParameter = null,
        string? providerRequestId = null
    ) : base(message) {
        Reason = reason;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        ProviderErrorCode = providerErrorCode;
        ProviderErrorType = providerErrorType;
        ProviderErrorParameter = providerErrorParameter;
        ProviderRequestId = providerRequestId;
    }

    public OpenAICodexResponsesFailureReason Reason { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Strictly bounded opaque provider error token, when the non-success JSON
    /// body contained an <c>error.code</c> matching the transport character
    /// policy. It remains provider-controlled and may be sensitive: do not log
    /// or persist it. It is never included in <see cref="Exception.Message"/>
    /// or <see cref="Exception.ToString"/>.
    /// </summary>
    public string? ProviderErrorCode { get; }

    /// <summary>
    /// Strictly bounded opaque provider error category. It remains
    /// provider-controlled and may be sensitive; do not log or persist it.
    /// </summary>
    public string? ProviderErrorType { get; }

    /// <summary>
    /// Strictly bounded opaque provider parameter path. It remains
    /// provider-controlled and may be sensitive; do not log or persist it.
    /// </summary>
    public string? ProviderErrorParameter { get; }

    /// <summary>
    /// Strictly bounded opaque request identifier from a recognized response
    /// header. It remains provider-controlled and may be sensitive; do not log
    /// or persist it.
    /// </summary>
    public string? ProviderRequestId { get; }

}
