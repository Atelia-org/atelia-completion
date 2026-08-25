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
    /// Strictly bounded provider error token, when the non-success JSON body
    /// contained a safe <c>error.code</c>. Provider messages are never retained.
    /// </summary>
    public string? ProviderErrorCode { get; }

    /// <summary>
    /// Strictly bounded provider error category, when available as a safe
    /// <c>error.type</c> token.
    /// </summary>
    public string? ProviderErrorType { get; }

    /// <summary>
    /// Strictly bounded provider parameter path, when available as a safe
    /// <c>error.param</c> token.
    /// </summary>
    public string? ProviderErrorParameter { get; }

    /// <summary>
    /// Strictly bounded request identifier from a recognized response header.
    /// </summary>
    public string? ProviderRequestId { get; }

}
