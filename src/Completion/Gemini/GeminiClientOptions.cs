using Atelia.Completion.Abstractions;
using Atelia.Completion.ModelSpecs;

namespace Atelia.Completion.Gemini;

public sealed class GeminiClientOptions {
    public CompletionReasoningEffort ReasoningEffort { get; init; } =
        CompletionReasoningEffort.ProviderDefault;

    /// <summary>
    /// Overrides Generate Content model knowledge. Null uses the built-in catalog;
    /// an explicit empty catalog disables that knowledge. Explicit effort requires
    /// a mapper for the selected model; this adapter projects thinking levels only.
    /// </summary>
    public CompletionModelSpecCatalog? ModelSpecs { get; init; }
}
