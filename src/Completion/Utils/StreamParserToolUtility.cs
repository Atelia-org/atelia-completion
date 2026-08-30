using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Utils;

/// <summary>
/// StreamParser 共享静态工具。
/// </summary>
internal static class StreamParserToolUtility {
    /// <summary>
    /// 构造只携带原始 JSON 文本的 <see cref="RawToolCall"/>。
    /// </summary>
    public static RawToolCall BuildToolCallWithoutSchema(string toolName, string toolCallId, string rawArgumentsText) {
        return new RawToolCall(
            ToolName: toolName,
            ToolCallId: toolCallId,
            RawArgumentsJson: NormalizeRawArgumentsJson(rawArgumentsText)
        );
    }

    /// <summary>
    /// 将空白参数载荷归一化为 <c>{}</c>，以保证 history/replay 形状稳定。
    /// </summary>
    public static string NormalizeRawArgumentsJson(string? rawArgumentsText)
        => string.IsNullOrWhiteSpace(rawArgumentsText) ? "{}" : rawArgumentsText;
}
