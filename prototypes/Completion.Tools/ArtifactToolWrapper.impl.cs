using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools.Declaration;

namespace Atelia.Completion.Tools;

partial class ArtifactToolWrapper<T> {
    private readonly ToolDefinition _definition;
    private readonly Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> _executor;

    private ArtifactToolWrapper(
        ToolDefinition definition,
        Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> executor
    ) {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    private static partial ArtifactToolWrapper<T> Bind(string toolName, ArtifactHandler<T> handler) {
        if (string.IsNullOrWhiteSpace(toolName)) { throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(toolName)); }
        ArgumentNullException.ThrowIfNull(handler);

        var definition = ReflectedToolDefinitionBuilder.BuildDefinitionUsingTypeDescription(toolName, typeof(T));
        var inputSchema = definition.InputSchema as ToolSchema.Object
            ?? throw new InvalidOperationException($"Tool '{toolName}' must expose an object input schema.");
        var executor = ObjectInputToolRuntime.CreateExecutor<T>(
            definition,
            inputSchema,
            (artifact, context, cancellationToken) => {
                _ = cancellationToken;

                var executionSequence = context.ExecutionSequence;
                var handlerResult = handler(artifact, context);
                var result = handlerResult.IsValid
                    ? new ToolExecuteResult(
                        ToolExecutionStatus.Success,
                        string.IsNullOrWhiteSpace(handlerResult.message)
                            ? $"产物已接收。sequence={executionSequence}"
                            : handlerResult.message
                    )
                    : new ToolExecuteResult(
                        ToolExecutionStatus.Failed,
                        string.IsNullOrWhiteSpace(handlerResult.message)
                            ? "产物校验失败。"
                            : $"产物校验失败。\n原因: {handlerResult.message}"
                    );

                return ValueTask.FromResult(result);
            }
        );

        return new ArtifactToolWrapper<T>(definition, executor);
    }

    public partial ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
        return _executor(context, cancellationToken);
    }
}
