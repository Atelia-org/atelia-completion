using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

public sealed record ToolExecutionContext {
    public ToolExecutionContext(
        ToolSessionState session,
        RawToolCall rawToolCall,
        long executionSequence
    ) {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        RawToolCall = rawToolCall ?? throw new ArgumentNullException(nameof(rawToolCall));
        if (executionSequence <= 0) { throw new ArgumentOutOfRangeException(nameof(executionSequence), executionSequence, "Execution sequence must be greater than zero."); }

        ExecutionSequence = executionSequence;
    }

    public ToolSessionState Session { get; }

    public RawToolCall RawToolCall { get; }

    public long ExecutionSequence { get; }

    public IServiceProvider? Services => Session.Services;

    public IReadOnlyDictionary<string, object?>? Items => Session.Items;
}

public sealed record ToolExecuteResult {
    public ToolExecuteResult(
        ToolExecutionStatus status,
        string content
    ) {
        Status = status;
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public ToolExecutionStatus Status { get; }

    public string Content { get; }
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
