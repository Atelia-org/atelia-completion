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
        CompletionReasoningEffort.Low => Enabled("low"),
        CompletionReasoningEffort.Medium => Enabled("medium"),
        CompletionReasoningEffort.High => Enabled("high"),
        CompletionReasoningEffort.Max => Enabled("xhigh"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(reasoningEffort),
            reasoningEffort,
            "Unknown reasoning effort."
        )
    };

    private static OpenAIResponsesReasoningConfig Enabled(string effort) =>
        new() { Effort = effort, Summary = "auto" };
}
