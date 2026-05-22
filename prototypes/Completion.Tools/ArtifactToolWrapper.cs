using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

/// <summary>
/// AI TODO:
/// 背景源于观察到“让LLM输出一个结构化产物”是一种常见任务，传统做法是让LLM在最终回复正文中只输出JSON，但这种做法有许多弊端。
/// 实践中我们找到的更好方案是将结构化产物包装为工具和工具调用，工具说明要产出个什么样的东西，工具调用可产出多份或异构产物。
/// 还可以无缝接入工具调用循环。主流模型都经过专门的工具调用训练，更加可靠。也不怕模型在最终恢复时附加前导语或结束语。
///
/// 这个类型的功能就是依托反射和ExpressionTree等底层技术，对外提供一种便捷方式，将业务代码需要的结构化产物（用带有Attribute的class）包装为可供LLM调用的<see cref="ITool"/>。
/// 业务逻辑代码通过<see cref="ArtifactHandler{T}"/>获取LLM输出的产物，以及进行可选的validation。
/// 本类型是<see cref="MethodToolWrapper"/>的姐妹类型。
/// 通过Attribute 声明式的为 class / record class 创建<see cref="ToolDefinition"/>的代码位于 Completion.Tools.Declaration 中，便于与 Artifact/Method tool wrapper 一起复用。
/// </summary>
public partial class ArtifactToolWrapper<T> : ITool where T : class {
    public static ArtifactToolWrapper<T> Create(string toolName, ArtifactHandler<T> handler) {
        return Bind(toolName, handler);
    }

    private static partial ArtifactToolWrapper<T> Bind(string toolName, ArtifactHandler<T> handler);

    public ToolDefinition Definition => _definition;

    public bool Visible { get; set; }

    public partial ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}

public record struct ValidateResult(bool IsValid, string? message);

public delegate ValidateResult ArtifactHandler<T>(T artifact, ToolExecutionContext context) where T : class;
