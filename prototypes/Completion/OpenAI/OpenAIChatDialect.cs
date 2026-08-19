namespace Atelia.Completion.OpenAI;

public enum OpenAIChatWhitespaceContentMode {
    Preserve,
    IgnoreWhitespaceDuringToolCalls
}

public enum OpenAIChatReasoningMode {
    Ignore,
    CaptureOnly,
    ReplayCompatible
}

public enum OpenAIChatReasoningControlMode {
    Unsupported,
    OpenAIReasoningEffort,
    QwenThinkingSwitch,
    DeepSeekV4ReasoningEffort,
}

public enum OpenAIChatUsageShape {
    OpenAIPromptTokenDetails,
    DeepSeekPromptCacheHitMiss,
}

public sealed record OpenAIChatDialect(
    string Name,
    OpenAIChatWhitespaceContentMode WhitespaceContentMode,
    OpenAIChatReasoningMode ReasoningMode = OpenAIChatReasoningMode.Ignore,
    OpenAIChatReasoningControlMode ReasoningControlMode =
        OpenAIChatReasoningControlMode.Unsupported,
    bool RequestStreamUsage = false,
    OpenAIChatUsageShape UsageShape =
        OpenAIChatUsageShape.OpenAIPromptTokenDetails
);

public static class OpenAIChatDialects {
    public static OpenAIChatDialect Strict { get; } = new(
        Name: "strict",
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.Preserve,
        ReasoningMode: OpenAIChatReasoningMode.CaptureOnly,
        ReasoningControlMode: OpenAIChatReasoningControlMode.OpenAIReasoningEffort,
        RequestStreamUsage: true
    );

    public static OpenAIChatDialect SgLangCompatible { get; } = new(
        Name: "sglang-compatible",
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls,
        ReasoningMode: OpenAIChatReasoningMode.Ignore
    );

    public static OpenAIChatDialect QwenSgLang { get; } = new(
        Name: "qwen-sglang",
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls,
        ReasoningMode: OpenAIChatReasoningMode.CaptureOnly,
        ReasoningControlMode: OpenAIChatReasoningControlMode.QwenThinkingSwitch
    );

    public static OpenAIChatDialect DeepSeekV4 { get; } = new(
        Name: "deepseek-v4",
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.Preserve,
        ReasoningMode: OpenAIChatReasoningMode.ReplayCompatible,
        ReasoningControlMode: OpenAIChatReasoningControlMode.DeepSeekV4ReasoningEffort,
        RequestStreamUsage: true,
        UsageShape: OpenAIChatUsageShape.DeepSeekPromptCacheHitMiss
    );
}
