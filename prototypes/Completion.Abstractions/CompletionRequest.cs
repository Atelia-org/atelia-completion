using System.Collections.Immutable;

namespace Atelia.Completion.Abstractions;

public sealed class CompletionRequest {
    public CompletionRequest(
        string modelId,
        CompletionPromptPrefix promptPrefix,
        IReadOnlyList<IHistoryMessage> tailMessages,
        int? maxTokens = null
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
        if (maxTokens is <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxTokens),
                maxTokens,
                "Max tokens must be positive when specified."
            );
        }

        TailMessages = [.. tailMessages];
        MaxTokens = maxTokens;
    }

    public string ModelId { get; }

    public CompletionPromptPrefix PromptPrefix { get; }

    public ImmutableArray<IHistoryMessage> TailMessages { get; }

    public int? MaxTokens { get; }
}
