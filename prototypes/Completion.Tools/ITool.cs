using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

/// <summary>
/// 表示可被 LLM 或宿主运行时调用的工具定义。
/// </summary>
public interface ITool {
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ToolParamSpec> Parameters { get; }
    bool Visible { get; set; }
    ValueTask<ToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken);
}
