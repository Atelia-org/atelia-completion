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
        IReadOnlyList<ToolResultBlock> blocks
    ) {
        ArgumentNullException.ThrowIfNull(blocks);

        Status = status;
        Blocks = Array.AsReadOnly(blocks.ToArray());
    }

    public ToolExecutionStatus Status { get; }

    public IReadOnlyList<ToolResultBlock> Blocks { get; }

    public string GetFlattenedText() => string.Concat(
        Blocks.OfType<ToolResultBlock.Text>().Select(static block => block.Content)
    );

    public static ToolExecuteResult FromText(
        ToolExecutionStatus status,
        string content
    ) => new(
        status,
        new ToolResultBlock[] { new ToolResultBlock.Text(content ?? throw new ArgumentNullException(nameof(content))) }
    );
}

public sealed record ToolCallExecutionResult {
    public ToolCallExecutionResult(
        RawToolCall rawToolCall,
        ToolExecuteResult executeResult,
        TimeSpan elapsed = default
    ) {
        RawToolCall = rawToolCall ?? throw new ArgumentNullException(nameof(rawToolCall));
        ExecuteResult = executeResult ?? throw new ArgumentNullException(nameof(executeResult));
        Elapsed = elapsed;
    }

    public RawToolCall RawToolCall { get; init; }

    public ToolExecuteResult ExecuteResult { get; init; }

    public string ToolName => RawToolCall.ToolName;

    public string ToolCallId => RawToolCall.ToolCallId;

    public TimeSpan? Elapsed { get; init; }

    public ToolResult ToToolResult() => new(
        ToolName,
        ToolCallId,
        ExecuteResult.Status,
        ExecuteResult.Blocks
    );
}
