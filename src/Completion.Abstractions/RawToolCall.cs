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

public abstract record ToolResultBlock {
    private protected ToolResultBlock() { }

    public abstract ToolResultBlockKind Kind { get; }

    public sealed record Text(string Content) : ToolResultBlock {
        public override ToolResultBlockKind Kind => ToolResultBlockKind.Text;
    }
}

public enum ToolResultBlockKind {
    Text
}

public sealed record ToolResult {
    public string ToolName { get; }

    public string ToolCallId { get; }

    public ToolExecutionStatus Status { get; }

    public IReadOnlyList<ToolResultBlock> Blocks { get; }

    public ToolResult(
        string toolName,
        string toolCallId,
        ToolExecutionStatus status,
        IReadOnlyList<ToolResultBlock> blocks
    ) {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Any(static block => block is null)) {
            throw new ArgumentException("Tool result blocks cannot contain null elements.", nameof(blocks));
        }

        ToolName = toolName;
        ToolCallId = toolCallId;
        Status = status;
        Blocks = Array.AsReadOnly(blocks.ToArray());
    }

    public string GetFlattenedText() => string.Concat(
        Blocks.OfType<ToolResultBlock.Text>().Select(static block => block.Content)
    );

    public static ToolResult FromText(
        string toolName,
        string toolCallId,
        ToolExecutionStatus status,
        string content
    ) => new(
        toolName: toolName,
        toolCallId: toolCallId,
        status: status,
        blocks: new ToolResultBlock[] { new ToolResultBlock.Text(content) }
    );
}
