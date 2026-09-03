using Atelia.Completion.Abstractions;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIResponsesClientOptions {
    public CompletionReasoningEffort ReasoningEffort { get; init; } =
        CompletionReasoningEffort.ProviderDefault;

    public bool Store { get; init; } = false;

    public bool IncludeEncryptedReasoning { get; init; } = true;
}
