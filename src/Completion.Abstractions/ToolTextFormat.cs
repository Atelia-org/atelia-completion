namespace Atelia.Completion.Abstractions;

public enum ToolInputKind {
    JsonObject,
    Text
}

/// <summary>Provider generation constraints for raw text. Execution does not validate this grammar.</summary>
public sealed record ToolTextFormat {
    private ToolTextFormat(string? syntax, string? definition) {
        Syntax = syntax;
        Definition = definition;
    }

    public string? Syntax { get; }
    public string? Definition { get; }
    public static ToolTextFormat Unconstrained { get; } = new(null, null);

    public static ToolTextFormat Grammar(string syntax, string definition) {
        if (syntax is not ("lark" or "regex")) {
            throw new ArgumentException("Grammar syntax must be lark or regex.", nameof(syntax));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
        return new(syntax, definition);
    }
}
