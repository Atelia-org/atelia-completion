using System.Collections.Immutable;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// Describes one Completion invocation without imposing a caller-selected
/// output-token ceiling.
/// </summary>
/// <remarks>
/// Per-request and per-connection output caps are intentionally unsupported.
/// Provider limit fields are omitted when omission has maximum/unlimited
/// semantics. When a numeric field is required to realize those semantics,
/// including when omission selects a lower model-varying default, it is
/// populated only with the selected model's provider-reported maximum. This
/// prevents a local budget from truncating an already billable generation
/// before it produces a usable result.
/// </remarks>
public sealed class CompletionRequest {
    /// <summary>Creates an immutable request for the selected model.</summary>
    /// <remarks>
    /// There is deliberately no output-token-limit parameter. See the class
    /// contract for the provider mapping policy.
    /// </remarks>
    public CompletionRequest(
        string modelId,
        CompletionPromptPrefix promptPrefix,
        IReadOnlyList<IHistoryMessage> tailMessages
    ) {
        ModelId = string.IsNullOrWhiteSpace(modelId)
            ? throw new ArgumentException("Model id cannot be empty.", nameof(modelId))
            : modelId;
        PromptPrefix = promptPrefix
            ?? throw new ArgumentNullException(nameof(promptPrefix));
        ArgumentNullException.ThrowIfNull(tailMessages);
        if (tailMessages.Any(static message => message is null)) {
            throw new ArgumentException(
                "Tail messages cannot contain null elements.",
                nameof(tailMessages)
            );
        }
        TailMessages = [.. tailMessages];
    }

    public string ModelId { get; }

    public CompletionPromptPrefix PromptPrefix { get; }

    public ImmutableArray<IHistoryMessage> TailMessages { get; }
}
