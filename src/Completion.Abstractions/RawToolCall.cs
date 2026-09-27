namespace Atelia.Completion.Abstractions;

/// <summary>A provider tool invocation with an explicit input protocol.</summary>
public record RawToolCall {
    // Preserve the established JSON constructor and named arguments.
    public RawToolCall(string ToolName, string ToolCallId, string RawArgumentsJson)
        : this(ToolName, ToolCallId, RawArgumentsJson, ToolInputKind.JsonObject) { }

    private RawToolCall(string toolName, string toolCallId, string rawInput, ToolInputKind inputKind) {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentNullException.ThrowIfNull(rawInput);
        ToolName = toolName;
        ToolCallId = toolCallId;
        _rawInput = rawInput;
        InputKind = inputKind;
    }

    public string ToolName { get; init; }
    public string ToolCallId { get; init; }
    public ToolInputKind InputKind { get; }
    private readonly string _rawInput;
    public string RawInput => _rawInput;
    public string RawArgumentsJson {
        get => InputKind == ToolInputKind.JsonObject
            ? RawInput
            : throw new InvalidOperationException("Text tool input is not JSON arguments; use RawInput.");
        init {
            if (InputKind != ToolInputKind.JsonObject) {
                throw new InvalidOperationException("Text tool input cannot be replaced with JSON arguments.");
            }
            _rawInput = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    protected virtual bool PrintMembers(System.Text.StringBuilder builder) {
        builder.Append("ToolName = ").Append(ToolName)
            .Append(", ToolCallId = ").Append(ToolCallId)
            .Append(", InputKind = ").Append(InputKind);
        return true;
    }

    public static RawToolCall FromText(string toolName, string toolCallId, string text)
        => new(toolName, toolCallId, text, ToolInputKind.Text);

    public void Deconstruct(out string toolName, out string toolCallId, out string rawArgumentsJson) {
        toolName = ToolName;
        toolCallId = ToolCallId;
        rawArgumentsJson = RawArgumentsJson;
    }
}

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
