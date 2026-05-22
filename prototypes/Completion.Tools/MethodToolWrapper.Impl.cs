using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools.Declaration;

namespace Atelia.Completion.Tools;

partial class MethodToolWrapper {
    private readonly ToolDefinition _definition;
    private readonly Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> _executor;

    private MethodToolWrapper(
        ToolDefinition definition,
        Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> executor
    ) {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        => _executor(context, cancellationToken);
}

partial class MethodToolWrapper {
    internal static MethodToolWrapper FromMethodImpl(object? targetInstance, MethodInfo method) {
        if (method is null) { throw new ArgumentNullException(nameof(method)); }

        var toolAttribute = method.GetCustomAttribute<ToolAttribute>()
            ?? throw new InvalidOperationException($"Method '{method.DeclaringType?.FullName}.{method.Name}' is missing ToolAttribute.");

        if (method.ReturnType != typeof(ValueTask<ToolExecuteResult>)) { throw new InvalidOperationException($"Method '{method.Name}' must return ValueTask<ToolExecuteResult>."); }

        var parameters = method.GetParameters();
        if (parameters.Length != 3) {
            throw new InvalidOperationException(
                $"Method '{method.Name}' must declare exactly one business input parameter followed by ToolExecutionContext and CancellationToken."
            );
        }

        if (parameters[^2].ParameterType != typeof(ToolExecutionContext)) {
            throw new InvalidOperationException($"Method '{method.Name}' must declare ToolExecutionContext as the second-to-last parameter.");
        }

        if (parameters[^1].ParameterType != typeof(CancellationToken)) { throw new InvalidOperationException($"Method '{method.Name}' must declare CancellationToken as the last parameter."); }

        if (!method.IsStatic && targetInstance is null) { throw new InvalidOperationException($"Instance method '{method.Name}' requires a target instance."); }

        if (!method.IsStatic && targetInstance is not null && method.DeclaringType is not null && !method.DeclaringType.IsInstanceOfType(targetInstance)) { throw new InvalidOperationException($"Target instance for method '{method.Name}' must be assignable to '{method.DeclaringType.FullName}'."); }

        var inputParameter = parameters[0];
        if (inputParameter.ParameterType.IsByRef) { throw new NotSupportedException($"Parameter '{inputParameter.Name}' on method '{method.Name}' cannot be passed by reference."); }

        var inputSchema = ReflectedToolDefinitionBuilder.BuildInputObjectSchema(inputParameter.ParameterType);
        var definition = new ToolDefinition(toolAttribute.Name, toolAttribute.Description, inputSchema);
        var invoker = CreateInvoker(method, method.IsStatic ? null : targetInstance, inputParameter.ParameterType);
        var executor = ObjectInputToolRuntime.CreateExecutor(inputParameter.ParameterType, definition, inputSchema, invoker);

        return new MethodToolWrapper(
            definition,
            executor
        );
    }

    private static Func<object, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> CreateInvoker(
        MethodInfo method,
        object? targetInstance,
        Type inputType
    ) {
        var inputParameter = Expression.Parameter(typeof(object), "input");
        var toolExecutionContextParameter = Expression.Parameter(typeof(ToolExecutionContext), "context");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        var callArguments = new Expression[] {
            Expression.Convert(inputParameter, inputType),
            toolExecutionContextParameter,
            cancellationTokenParameter
        };

        Expression? instanceExpression = null;
        if (!method.IsStatic) {
            instanceExpression = Expression.Constant(targetInstance, method.DeclaringType ?? targetInstance?.GetType() ?? throw new InvalidOperationException($"Method '{method.Name}' requires a target instance."));
        }

        var callExpression = Expression.Call(instanceExpression, method, callArguments);

        return Expression
            .Lambda<Func<object, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>>>(
            callExpression,
            inputParameter,
            toolExecutionContextParameter,
            cancellationTokenParameter
        )
            .Compile();
    }
}
