namespace Atelia.Completion.Abstractions;

public enum CompletionChunkKind {
    Content,
    ToolCall,
    // ServerToolExecuate ToolResultProduced, 其实有的Provider是支持服务器端工具执行的，比如搜索和代码沙盒。但我们下游实现尚未准备好，作为高级特性，暂时不支持。
    Error,
    TokenUsage
}

public sealed record CompletionChunk {
    public CompletionChunkKind Kind { get; init; }
    public string? Content { get; init; }
    public ParsedToolCall? ToolCall { get; init; }
    // ServerToolExecuate public ToolCallResult? ToolCallResult { get; init; }
    public string? Error { get; init; }
    public TokenUsage? TokenUsage { get; init; }

    public static CompletionChunk FromContent(string fragment)
        => new() { Kind = CompletionChunkKind.Content, Content = fragment };

    public static CompletionChunk FromToolCall(ParsedToolCall request)
        => new() { Kind = CompletionChunkKind.ToolCall, ToolCall = request };

    // ServerToolExecuate public static CompletionChunk ToolResult(ToolCallResult result)
    //     => new() { Kind = CompletionChunkKind.ToolResultProduced, ToolCallResult = result };

    public static CompletionChunk FromError(string error)
        => new() { Kind = CompletionChunkKind.Error, Error = error };

    public static CompletionChunk FromTokenUsage(TokenUsage usage)
        => new() { Kind = CompletionChunkKind.TokenUsage, TokenUsage = usage };
}
