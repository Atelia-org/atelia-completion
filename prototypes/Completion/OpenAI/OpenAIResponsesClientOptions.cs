using System.Text.Json.Nodes;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIResponsesClientOptions {
    public bool Store { get; init; } = false;

    public bool IncludeEncryptedReasoning { get; init; } = true;

    public bool ParallelToolCalls { get; init; } = true;

    public JsonObject? ExtraBody { get; init; }
}
