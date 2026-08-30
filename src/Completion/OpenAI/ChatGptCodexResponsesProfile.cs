namespace Atelia.Completion.OpenAI;

internal static class ChatGptCodexResponsesProfile {
    public const string ConnectionKind = "openai-codex-responses";
    public const string CompletionSurfaceId = "openai-codex-responses";
    public const string ApiSpecId = "openai-codex-responses-v2";
    public const string ReasoningMappingId =
        "openai-codex-responses-effort-v1";
    public const string CanonicalBaseAddressText =
        "https://chatgpt.com/backend-api/codex/";
    public const string RelativeRequestUri = "responses";

    public static Uri CanonicalBaseAddress { get; } = new(
        CanonicalBaseAddressText,
        UriKind.Absolute
    );

    public static OpenAIResponsesReasoningConfig? MapReasoningEffort(
        Atelia.Completion.Abstractions.CompletionReasoningEffort effort
    ) => OpenAIResponsesReasoningWireMapping.MapCurrent(effort);
}
