namespace Atelia.Completion.Abstractions;

public enum CompletionChunkKind {
    Content,
    ToolCall,
    Thinking,
    // ServerToolExecuate ToolResultProduced, 其实有的Provider是支持服务器端工具执行的，比如搜索和代码沙盒。但我们下游实现尚未准备好，作为高级特性，暂时不支持。
    Error,
    TokenUsage
}

public sealed record CompletionChunk {
    public CompletionChunkKind Kind { get; init; }
    public string? Content { get; init; }
    public ParsedToolCall? ToolCall { get; init; }
    public ThinkingChunk? Thinking { get; init; }
    // ServerToolExecuate public ToolCallResult? ToolCallResult { get; init; }
    public string? Error { get; init; }
    public TokenUsage? TokenUsage { get; init; }

    public static CompletionChunk FromContent(string fragment)
        => new() { Kind = CompletionChunkKind.Content, Content = fragment };

    public static CompletionChunk FromToolCall(ParsedToolCall request)
        => new() { Kind = CompletionChunkKind.ToolCall, ToolCall = request };

    public static CompletionChunk FromThinking(ThinkingChunk thinking)
        => new() { Kind = CompletionChunkKind.Thinking, Thinking = thinking };

    // ServerToolExecuate public static CompletionChunk ToolResult(ToolCallResult result)
    //     => new() { Kind = CompletionChunkKind.ToolResultProduced, ToolCallResult = result };

    public static CompletionChunk FromError(string error)
        => new() { Kind = CompletionChunkKind.Error, Error = error };

    public static CompletionChunk FromTokenUsage(TokenUsage usage)
        => new() { Kind = CompletionChunkKind.TokenUsage, TokenUsage = usage };
}

/// <summary>
/// StreamParser 完成一个 thinking content block 的聚合后产出。
/// <see cref="OpaquePayload"/> 由 parser 直接以 provider-native bytes 形式构造，
/// Agent.Core / <c>CompletionAccumulator</c> 不参与解释——这条边界是"为什么 Agent.Core
/// 不会被 provider 细节污染"的真正担保。详见 <c>docs/Agent/Thinking-Replay-Design.md §5.2</c>。
/// </summary>
/// <param name="OpaquePayload">Provider-native 序列化字节，由 converter 反向回灌时按需反序列化。</param>
/// <param name="PlainTextForDebug">可选明文，仅供日志/UI/调试使用，<b>不参与回灌</b>。</param>
public sealed record ThinkingChunk(
    System.ReadOnlyMemory<byte> OpaquePayload,
    string? PlainTextForDebug = null
);

