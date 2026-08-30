using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

/// <summary>
/// 表示可被 LLM 或宿主运行时调用的工具定义。
/// </summary>
public interface ITool {
    ToolDefinition Definition { get; }
    ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}
