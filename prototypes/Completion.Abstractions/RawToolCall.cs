namespace Atelia.Completion.Abstractions;

/// <summary>
/// Captures a raw tool invocation emitted by a provider.
/// </summary>
/// <param name="ToolName">Logical tool identifier selected by the model.</param>
/// <param name="ToolCallId">Provider specific identifier used to correlate execution results.</param>
/// <param name="RawArgumentsJson">
/// Raw JSON arguments text captured from the provider transport. Providers should preserve the original text when available;
/// if the model omits all parameters, use <c>{}</c> instead of <see langword="null"/>.
/// </param>
public record RawToolCall(
    string ToolName,
    string ToolCallId,
    string RawArgumentsJson
);

public enum ToolExecutionStatus {
    Success,
    Failed,
    Skipped
}

public record ToolResult(
    string ToolName,
    string ToolCallId,
    ToolExecutionStatus Status,
    string Result
);
