using System.Text;

namespace Atelia.Completion.Tools;

/// <summary>
/// The tool may have committed an external effect, but cannot produce a
/// terminal result yet. Durable hosts must leave ToolExecutionStarted as the
/// current uncertain boundary and retry/reconcile with the same operation id.
/// </summary>
public sealed class ToolExecutionUnsettledException : Exception {
    public ToolExecutionUnsettledException(string code, string detail)
        : base(RequireBounded(detail, nameof(detail), 4 * 1024)) {
        Code = RequireBounded(code, nameof(code), 128);
    }

    public string Code { get; }

    private static string RequireBounded(
        string value,
        string parameterName,
        int maximumUtf8Bytes
    ) {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)) {
            throw new ArgumentException(
                "A non-empty canonical value is required.",
                parameterName
            );
        }
        try {
            if (new UTF8Encoding(false, true).GetByteCount(value)
                > maximumUtf8Bytes) {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "The value must be strict UTF-8 text.",
                parameterName,
                exception
            );
        }
        return value;
    }
}

/// <summary>
/// Explicit proof from a tool that caller cancellation was observed before
/// any external mutation began. Only this cancellation is safe to journal as
/// a terminal Skipped tool result.
/// </summary>
public sealed class ToolExecutionCancelledBeforeMutationException
    : OperationCanceledException {
    public ToolExecutionCancelledBeforeMutationException(
        CancellationToken cancellationToken
    ) : base("Tool execution was cancelled before mutation.",
        cancellationToken) { }
}
