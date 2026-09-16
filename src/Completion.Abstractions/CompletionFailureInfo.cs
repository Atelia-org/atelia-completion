namespace Atelia.Completion.Abstractions;

/// <summary>Observed failure facts; retry policy belongs to the caller.</summary>
public enum CompletionFailureKind { Transport, Http, Provider }

/// <summary>Metadata only. No remote execution or retry-safety guarantee is implied.</summary>
public sealed record CompletionFailureInfo(
    CompletionFailureKind Kind,
    int? HttpStatusCode = null,
    string? ProviderCode = null,
    TimeSpan? RetryAfter = null
);

/// <summary>A classified HTTP or transport failure of one completion invocation.</summary>
public class CompletionFailureException : Exception {
    public CompletionFailureException(
        CompletionFailureInfo failure,
        string message,
        Exception? innerException = null
    ) : base(message, innerException) {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public CompletionFailureInfo Failure { get; }
}
