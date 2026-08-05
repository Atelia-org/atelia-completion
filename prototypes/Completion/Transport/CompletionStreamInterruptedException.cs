namespace Atelia.Completion.Transport;

/// <summary>
/// The response transport ended before the provider protocol supplied its
/// terminal event. This describes an incomplete observation of the remote
/// operation; it does not assert that the LLM operation failed.
/// </summary>
public sealed class CompletionStreamInterruptedException : IOException {
    public CompletionStreamInterruptedException(string streamDisplayName)
        : base(CreateMessage(streamDisplayName)) {
        StreamDisplayName = streamDisplayName;
    }

    public string StreamDisplayName { get; }

    private static string CreateMessage(string streamDisplayName) {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamDisplayName);
        return $"{streamDisplayName} transport stream ended before a "
            + "provider terminal event was received; the remote operation "
            + "outcome is uncertain.";
    }
}

internal static class CompletionStreamTermination {
    public static void RequireTerminalEvent(
        bool terminalEventObserved,
        string streamDisplayName
    ) {
        if (!terminalEventObserved) {
            throw new CompletionStreamInterruptedException(
                streamDisplayName
            );
        }
    }
}
