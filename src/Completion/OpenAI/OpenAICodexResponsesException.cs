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
        string? providerRequestId = null,
        Exception? innerException = null
    ) : base(message, innerException) {
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
    /// Provider error code from the non-success JSON response, when present.
    /// </summary>
    public string? ProviderErrorCode { get; }

    /// <summary>
    /// Provider error category from the non-success JSON response.
    /// </summary>
    public string? ProviderErrorType { get; }

    /// <summary>
    /// Provider parameter path from the non-success JSON response.
    /// </summary>
    public string? ProviderErrorParameter { get; }

    /// <summary>
    /// Request identifier from a recognized response header.
    /// </summary>
    public string? ProviderRequestId { get; }

}
