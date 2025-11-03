namespace Atelia.LlmProviders;

public enum ModelOutputDeltaKind {
    Content,
    ToolCallDeclared,
    ToolResultProduced,
    ExecuteError,
    TokenUsage
}

public sealed record ModelOutputDelta {
    public ModelOutputDeltaKind Kind { get; init; }
    public string? ContentFragment { get; init; }
    public bool EndSegment { get; init; }
    public ToolCallRequest? ToolCallRequest { get; init; }
    public ToolCallResult? ToolCallResult { get; init; }
    public string? ExecuteError { get; init; }
    public TokenUsage? TokenUsage { get; init; }

    public static ModelOutputDelta Content(string fragment, bool endSegment = false)
        => new() { Kind = ModelOutputDeltaKind.Content, ContentFragment = fragment, EndSegment = endSegment };

    public static ModelOutputDelta ToolCall(ToolCallRequest request)
        => new() { Kind = ModelOutputDeltaKind.ToolCallDeclared, ToolCallRequest = request };

    public static ModelOutputDelta ToolResult(ToolCallResult result)
        => new() { Kind = ModelOutputDeltaKind.ToolResultProduced, ToolCallResult = result };

    public static ModelOutputDelta ExecutionError(string error)
        => new() { Kind = ModelOutputDeltaKind.ExecuteError, ExecuteError = error };

    public static ModelOutputDelta Usage(TokenUsage usage)
        => new() { Kind = ModelOutputDeltaKind.TokenUsage, TokenUsage = usage };
}
