namespace Atelia.Completion.Abstractions;

/// <summary>
/// StreamParser 完成一个 thinking content block 的聚合后产出。
/// <see cref="OpaquePayload"/> 由 parser 直接以 provider-native bytes 形式构造，
/// Agent.Core / <c>CompletionAggregator</c> 不参与解释——这条边界是"为什么 Agent.Core
/// 不会被 provider 细节污染"的真正担保。详见 <c>docs/Agent/Thinking-Replay-Design.md §5.2</c>。
/// </summary>
/// <param name="OpaquePayload">Provider-native 序列化字节，由 converter 反向回灌时按需反序列化。</param>
/// <param name="PlainTextForDebug">可选明文，仅供日志/UI/调试使用，<b>不参与回灌</b>。</param>
public sealed record ThinkingChunk(
    System.ReadOnlyMemory<byte> OpaquePayload,
    string? PlainTextForDebug = null
);
