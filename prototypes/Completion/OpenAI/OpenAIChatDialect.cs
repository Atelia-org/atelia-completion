namespace Atelia.Completion.OpenAI;

// 当前刻意只保留单一成员：按 docs/Completion/openai-compatible-evolution.md 的“第 3 次差异才升级”
// 节奏，没有第二种已确认的投影方式之前，不引入新枚举值，避免占位代码诱导误删。
public enum OpenAIChatToolResultProjectionStyle {
    StrictToolMessagesThenTrailingUserObservation
}

public enum OpenAIChatStreamUsageMode {
    Disabled,
    RequestUsage,
    RequestUsageAndRetryWithoutStreamOptions
}

public enum OpenAIChatWhitespaceContentMode {
    Preserve,
    IgnoreWhitespaceDuringToolCalls
}

public sealed record OpenAIChatDialect(
    string Name,
    OpenAIChatToolResultProjectionStyle ToolResultProjectionStyle,
    OpenAIChatStreamUsageMode StreamUsageMode,
    OpenAIChatWhitespaceContentMode WhitespaceContentMode
);

public static class OpenAIChatDialects {
    public static OpenAIChatDialect Strict { get; } = new(
        Name: "strict",
        ToolResultProjectionStyle: OpenAIChatToolResultProjectionStyle.StrictToolMessagesThenTrailingUserObservation,
        StreamUsageMode: OpenAIChatStreamUsageMode.RequestUsage,
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.Preserve
    );

    public static OpenAIChatDialect SgLangCompatible { get; } = new(
        Name: "sglang-compatible",
        ToolResultProjectionStyle: OpenAIChatToolResultProjectionStyle.StrictToolMessagesThenTrailingUserObservation,
        StreamUsageMode: OpenAIChatStreamUsageMode.RequestUsageAndRetryWithoutStreamOptions,
        WhitespaceContentMode: OpenAIChatWhitespaceContentMode.IgnoreWhitespaceDuringToolCalls
    );
}
