namespace Atelia.Completion.Transport;

/// <summary>
/// The response transport ended before the provider protocol supplied its
/// terminal event. This describes an incomplete observation of the remote
/// operation; it does not assert that the LLM operation failed.
/// </summary>
public sealed class CompletionStreamInterruptedException : IOException {
    public CompletionStreamInterruptedException(
        string streamDisplayName,
        string? diagnosticContext = null
    )
        : base(CreateMessage(streamDisplayName, diagnosticContext)) {
        StreamDisplayName = streamDisplayName;
        DiagnosticContext = string.IsNullOrWhiteSpace(diagnosticContext)
            ? null
            : diagnosticContext;
    }

    public string StreamDisplayName { get; }
    public string? DiagnosticContext { get; }

    private static string CreateMessage(
        string streamDisplayName,
        string? diagnosticContext
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamDisplayName);
        var message = $"{streamDisplayName} transport stream ended before a "
            + "provider terminal event was received; the remote operation "
            + "outcome is uncertain.";
        return string.IsNullOrWhiteSpace(diagnosticContext)
            ? message
            : $"{message} Diagnostic context: {diagnosticContext}";
    }
}

internal static class CompletionStreamTermination {
    public static void RequireTerminalEvent(
        bool terminalEventObserved,
        string streamDisplayName,
        string? diagnosticContext = null
    ) {
        if (!terminalEventObserved) {
            throw new CompletionStreamInterruptedException(
                streamDisplayName,
                diagnosticContext
            );
        }
    }
}
