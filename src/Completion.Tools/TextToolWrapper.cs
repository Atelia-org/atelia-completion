using System.Reflection;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

/// <summary>Wraps a raw text tool without JSON parsing or whitespace normalization.</summary>
public sealed class TextToolWrapper : ITool {
    private readonly Func<string, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> _handler;

    private TextToolWrapper(ToolDefinition definition,
        Func<string, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> handler) {
        Definition = definition;
        _handler = handler;
    }

    public ToolDefinition Definition { get; }

    public static TextToolWrapper FromDelegate(
        Func<string, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> handler,
        ToolTextFormat? format = null
    ) {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler.GetInvocationList().Length != 1) {
            throw new ArgumentException("Delegate must reference exactly one method.", nameof(handler));
        }
        return FromMethod(handler.Target, handler.Method, format);
    }

    public static TextToolWrapper FromMethod(object? targetInstance, MethodInfo method, ToolTextFormat? format = null) {
        ArgumentNullException.ThrowIfNull(method);
        var attribute = method.GetCustomAttribute<ToolAttribute>()
            ?? throw new InvalidOperationException($"Method '{method.Name}' is missing ToolAttribute.");
        var parameters = method.GetParameters();
        if (method.ContainsGenericParameters || method.ReturnType != typeof(ValueTask<ToolExecuteResult>)
            || parameters.Length != 3 || parameters[0].ParameterType != typeof(string)
            || parameters[1].ParameterType != typeof(ToolExecutionContext)
            || parameters[2].ParameterType != typeof(CancellationToken)) {
            throw new InvalidOperationException("Text tool must have signature (string, ToolExecutionContext, CancellationToken) -> ValueTask<ToolExecuteResult>.");
        }
        if (!method.IsStatic && (targetInstance is null || !method.DeclaringType!.IsInstanceOfType(targetInstance))) {
            throw new InvalidOperationException($"Instance method '{method.Name}' requires a compatible target instance.");
        }
        var handler = method.IsStatic
            ? method.CreateDelegate<Func<string, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>>>()
            : method.CreateDelegate<Func<string, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>>>(targetInstance);
        return new(ToolDefinition.FromText(attribute.Name, attribute.Description, format), handler);
    }

    public ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.RawToolCall.InputKind != ToolInputKind.Text) {
            return ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Failed, "Tool requires raw text input."));
        }
        return _handler(context.RawToolCall.RawInput, context, cancellationToken);
    }
}
