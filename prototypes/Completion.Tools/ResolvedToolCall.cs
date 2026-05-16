namespace Atelia.Completion.Tools;

internal sealed record ResolvedToolCall(
    string ToolName,
    string ToolCallId,
    IReadOnlyDictionary<string, object?> Arguments,
    string? ParseError,
    string? ParseWarning
);
