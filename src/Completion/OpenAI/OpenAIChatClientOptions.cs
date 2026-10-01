using Atelia.Completion.Abstractions;
using Atelia.Completion.ModelSpecs;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIChatClientOptions {
    public CompletionReasoningEffort ReasoningEffort { get; init; } =
        CompletionReasoningEffort.ProviderDefault;

    /// <summary>
    /// Overrides the client/dialect model knowledge. Null uses its built-in catalog;
    /// an explicit empty catalog disables that knowledge.
    /// </summary>
    public CompletionModelSpecCatalog? ModelSpecs { get; init; }
}
