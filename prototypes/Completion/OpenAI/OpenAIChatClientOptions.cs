using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIChatClientOptions {
    public CompletionReasoningEffort ReasoningEffort { get; init; } =
        CompletionReasoningEffort.ProviderDefault;

    public JsonObject? ExtraBody { get; init; }
}
