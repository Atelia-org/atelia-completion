using Atelia.Completion.Abstractions;

namespace Atelia.Completion.OpenAI;

internal delegate OpenAIResponsesReasoningConfig?
    OpenAIResponsesReasoningMapper(CompletionReasoningEffort effort);

internal static class PublicOpenAIResponsesProfile {
    public const string ApiSpecId = "openai-responses-v2";

    public static OpenAIResponsesReasoningConfig? MapReasoningEffort(
        CompletionReasoningEffort effort
    ) => OpenAIResponsesReasoningWireMapping.MapCurrent(effort);
}

internal static class OpenAIResponsesReasoningWireMapping {
    public static OpenAIResponsesReasoningConfig? MapCurrent(
        CompletionReasoningEffort reasoningEffort
    ) => reasoningEffort switch {
        CompletionReasoningEffort.ProviderDefault => null,
        CompletionReasoningEffort.Disabled =>
            new OpenAIResponsesReasoningConfig { Effort = "none" },
        CompletionReasoningEffort.Low => new() { Effort = "low" },
        CompletionReasoningEffort.Medium => new() { Effort = "medium" },
        CompletionReasoningEffort.High => new() { Effort = "high" },
        CompletionReasoningEffort.XHigh => new() { Effort = "xhigh" },
        CompletionReasoningEffort.Max => new() { Effort = "max" },
        _ => throw new ArgumentOutOfRangeException(
            nameof(reasoningEffort),
            reasoningEffort,
            "Unknown reasoning effort."
        )
    };
}
