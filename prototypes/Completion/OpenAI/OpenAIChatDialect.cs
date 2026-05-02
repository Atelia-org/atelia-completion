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

public sealed record OpenAIChatDialect(
    string Name,
    OpenAIChatWhitespaceContentMode WhitespaceContentMode,
    OpenAIChatReasoningMode ReasoningMode = OpenAIChatReasoningMode.Ignore
);

public static class OpenAIChatDialects {
    public static OpenAIChatDialect Strict { get; } = new(
        Name: "strict",
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.Preserve,
        ReasoningMode: OpenAIChatReasoningMode.Ignore
    );

    public static OpenAIChatDialect SgLangCompatible { get; } = new(
        Name: "sglang-compatible",
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls,
        ReasoningMode: OpenAIChatReasoningMode.Ignore
    );

    public static OpenAIChatDialect DeepSeekV4 { get; } = new(
        Name: "deepseek-v4",
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.Preserve,
        ReasoningMode: OpenAIChatReasoningMode.ReplayCompatible
    );
}
