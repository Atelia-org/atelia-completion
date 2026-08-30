using System.Collections.Immutable;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// Immutable provider-neutral prefix shared by requests that differ only in their tail.
/// </summary>
public sealed class CompletionPromptPrefix {
    public CompletionPromptPrefix(
        string systemPrompt,
        CompletionOutputContract outputContract,
        IReadOnlyList<IHistoryMessage> sharedContextMessages
    ) {
        SystemPrompt = systemPrompt
            ?? throw new ArgumentNullException(nameof(systemPrompt));
        OutputContract = outputContract
            ?? throw new ArgumentNullException(nameof(outputContract));
        ArgumentNullException.ThrowIfNull(sharedContextMessages);
        if (sharedContextMessages.Any(static message => message is null)) {
            throw new ArgumentException(
                "Shared context messages cannot contain null elements.",
                nameof(sharedContextMessages)
            );
        }

        SharedContextMessages = [.. sharedContextMessages];
    }

    public string SystemPrompt { get; }

    public CompletionOutputContract OutputContract { get; }

    public ImmutableArray<IHistoryMessage> SharedContextMessages { get; }
}
