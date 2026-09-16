namespace Atelia.Completion.OpenAI;

public enum OpenAICodexResponsesFailureReason {
    ProtocolCompatibilityFailure,
}

public sealed class OpenAICodexResponsesException : Exception {
    internal OpenAICodexResponsesException(
        OpenAICodexResponsesFailureReason reason,
        string message
    ) : base(message) {
        Reason = reason;
    }

    public OpenAICodexResponsesFailureReason Reason { get; }

}
