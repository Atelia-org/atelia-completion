using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

public sealed record ToolExecuteResult {
    public ToolExecuteResult(
        ToolExecutionStatus status,
        string basic,
        string? detail = null
    ) {
        Status = status;
        Basic = basic ?? throw new ArgumentNullException(nameof(basic));
        Detail = detail ?? basic;
    }

    public ToolExecutionStatus Status { get; }

    public string Basic { get; }

    public string Detail { get; }
}

public sealed record ToolCallExecutionResult {
    public ToolCallExecutionResult(
        ToolExecuteResult executeResult,
        string toolName,
        string toolCallId,
        TimeSpan elapsed = default
    ) {
        ExecuteResult = executeResult ?? throw new ArgumentNullException(nameof(executeResult));
        ToolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
        ToolCallId = toolCallId ?? throw new ArgumentNullException(nameof(toolCallId));
        Elapsed = elapsed;
    }

    public ToolExecuteResult ExecuteResult { get; init; }

    public string ToolName { get; init; }

    public string ToolCallId { get; init; }

    public TimeSpan? Elapsed { get; init; }
}
