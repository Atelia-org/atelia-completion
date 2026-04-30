namespace Atelia.Completion.OpenAI;

public enum OpenAIChatWhitespaceContentMode {
    Preserve,
    IgnoreWhitespaceDuringToolCalls
}

public sealed record OpenAIChatDialect(
    string Name,
    OpenAIChatWhitespaceContentMode WhitespaceContentMode
);

public static class OpenAIChatDialects {
    public static OpenAIChatDialect Strict { get; } = new(
        Name: "strict",
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.Preserve
    );

    public static OpenAIChatDialect SgLangCompatible { get; } = new(
        Name: "sglang-compatible",
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls
    );
}
