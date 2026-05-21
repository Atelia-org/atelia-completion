using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

partial class MethodToolWrapper {
    internal sealed record ArgGetter(string Name, ParamDefault? DefaultValue) {
        public object? GetValue(IReadOnlyDictionary<string, object?>? arguments) {
            if (arguments is not null && arguments.TryGetValue(Name, out var value)) { return value; }

            if (DefaultValue.HasValue) { return DefaultValue.Value.Value; }

            throw new ArgumentException($"Missing required argument: {Name}", Name);
        }
    }

    private readonly ToolDefinition _definition;
    private readonly IReadOnlyList<ArgGetter> _argGetters;
    private readonly Func<object?[], CancellationToken, ValueTask<ToolExecuteResult>> _invoker;

    private MethodToolWrapper(
        ToolDefinition definition,
        IReadOnlyList<ArgGetter> argGetters,
        Func<object?[], CancellationToken, ValueTask<ToolExecuteResult>> invoker
    ) {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _argGetters = argGetters;
        _invoker = invoker;
    }

    public bool Visible { get; set; } = true;

    public ValueTask<ToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) {
        var args = BuildArgs(_argGetters, arguments);
        return _invoker(args, cancellationToken);
    }
}

partial class MethodToolWrapper {
    internal static MethodToolWrapper FromMethodImpl(object? targetInstance, MethodInfo method, object?[]? formatArgs) {
        if (method is null) { throw new ArgumentNullException(nameof(method)); }

        var toolAttribute = method.GetCustomAttribute<ToolAttribute>()
            ?? throw new InvalidOperationException($"Method '{method.DeclaringType?.FullName}.{method.Name}' is missing ToolAttribute.");

        var formattingArgs = formatArgs is { Length: > 0 }
            ? formatArgs
            : Array.Empty<object?>();

        if (method.ReturnType != typeof(ValueTask<ToolExecuteResult>)) { throw new InvalidOperationException($"Method '{method.Name}' must return ValueTask<ToolExecuteResult>."); }

        var parameters = method.GetParameters();
        if (parameters.Length == 0 || parameters[^1].ParameterType != typeof(CancellationToken)) { throw new InvalidOperationException($"Method '{method.Name}' must declare CancellationToken as the last parameter."); }

        if (!method.IsStatic && targetInstance is null) { throw new InvalidOperationException($"Instance method '{method.Name}' requires a target instance."); }

        if (!method.IsStatic && targetInstance is not null && method.DeclaringType is not null && !method.DeclaringType.IsInstanceOfType(targetInstance)) { throw new InvalidOperationException($"Target instance for method '{method.Name}' must be assignable to '{method.DeclaringType.FullName}'."); }

        var signatureParameters = parameters[..^1];
        var schemaProperties = new List<ToolSchema.Property>(signatureParameters.Length);
        var argGetters = new List<ArgGetter>(signatureParameters.Length);

        foreach (var parameter in signatureParameters) {
            if (parameter.ParameterType.IsByRef) { throw new NotSupportedException($"Parameter '{parameter.Name}' on method '{method.Name}' cannot be passed by reference."); }

            var parameterAttribute = parameter.GetCustomAttribute<ToolParamAttribute>()
                ?? throw new InvalidOperationException($"Parameter '{parameter.Name}' on method '{method.Name}' is missing ToolParamAttribute.");

            var displayName = parameter.Name ?? throw new InvalidOperationException($"Parameter name for '{method.Name}' cannot be inferred.");
            var valueKind = ResolveValueKind(parameter.ParameterType);
            var allowsNull = ResolveNullability(parameter);
            var defaultInfo = ResolveDefaultValue(parameter, allowsNull);
            var formattedDescription = parameterAttribute.FormatDescription(formattingArgs);
            var effectiveDescription = BuildDescription(formattedDescription, allowsNull, defaultInfo);

            schemaProperties.Add(
                new ToolSchema.Property(
                    displayName,
                    new ToolSchema.Value(
                        valueKind,
                        allowsNull,
                        defaultInfo.DefaultValue,
                        effectiveDescription
                    ),
                    isRequired: !defaultInfo.IsOptional
                )
            );
            argGetters.Add(new ArgGetter(displayName, defaultInfo.DefaultValue));
        }

        var methodName = toolAttribute.FormatName(formattingArgs);
        var methodDescription = toolAttribute.FormatDescription(formattingArgs);
        var definition = new ToolDefinition(methodName, methodDescription, new ToolSchema.Object(schemaProperties));

        var invoker = CreateInvoker(method, method.IsStatic ? null : targetInstance);

        return new MethodToolWrapper(
            definition,
            argGetters.ToArray(),
            invoker
        );
    }

    internal static object?[] BuildArgs(IReadOnlyList<ArgGetter> getters, IReadOnlyDictionary<string, object?>? arguments) {
        var args = new object?[getters.Count];
        for (var i = 0; i < getters.Count; ++i) {
            args[i] = getters[i].GetValue(arguments);
        }
        return args;
    }

    private static Func<object?[], CancellationToken, ValueTask<ToolExecuteResult>> CreateInvoker(MethodInfo method, object? targetInstance) {
        var parameters = method.GetParameters();

        var argsParameter = Expression.Parameter(typeof(object?[]), "args");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        var callArguments = new Expression[parameters.Length];

        for (var i = 0; i < parameters.Length - 1; ++i) {
            var parameter = parameters[i];
            var valueExpression = Expression.ArrayIndex(argsParameter, Expression.Constant(i));
            callArguments[i] = Expression.Convert(valueExpression, parameter.ParameterType);
        }

        callArguments[^1] = cancellationTokenParameter;

        Expression? instanceExpression = null;
        if (!method.IsStatic) {
            instanceExpression = Expression.Constant(targetInstance, method.DeclaringType ?? targetInstance?.GetType() ?? throw new InvalidOperationException($"Method '{method.Name}' requires a target instance."));
        }

        var callExpression = Expression.Call(instanceExpression, method, callArguments);

        return Expression
            .Lambda<Func<object?[], CancellationToken, ValueTask<ToolExecuteResult>>>(
                callExpression,
                argsParameter,
                cancellationTokenParameter
            )
            .Compile();
    }

    private static ToolParamType ResolveValueKind(Type parameterType) {
        var underlying = Nullable.GetUnderlyingType(parameterType) ?? parameterType;

        if (underlying == typeof(string)) { return ToolParamType.String; }
        if (underlying == typeof(bool)) { return ToolParamType.Boolean; }
        if (underlying == typeof(int)) { return ToolParamType.Int32; }
        if (underlying == typeof(long)) { return ToolParamType.Int64; }
        if (underlying == typeof(float)) { return ToolParamType.Float32; }
        if (underlying == typeof(double)) { return ToolParamType.Float64; }
        if (underlying == typeof(decimal)) { return ToolParamType.Decimal; }

        throw new NotSupportedException($"Unsupported parameter type '{parameterType.FullName}'.");
    }

    private static bool ResolveNullability(ParameterInfo parameter) => AllowsNull(parameter);

    private static DefaultValueInfo ResolveDefaultValue(ParameterInfo parameter, bool allowsNull) {
        if (!parameter.HasDefaultValue) { return DefaultValueInfo.None; }

        var candidate = NormalizeDefaultValue(parameter.DefaultValue);

        if (candidate is null && !allowsNull) { throw new InvalidOperationException($"Default value for parameter '{parameter.Name}' on method '{parameter.Member?.Name}' cannot be null when parameter is not nullable."); }

        var defaultText = DescribeDefaultValue(candidate);
        return new DefaultValueInfo(new ParamDefault(candidate), defaultText);
    }

    private static object? NormalizeDefaultValue(object? value)
        => value is DBNull or Missing ? null : value;

    private static bool AllowsNull(ParameterInfo parameter) {
        if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null) { return true; }

        if (!parameter.ParameterType.IsValueType) {
            var nullability = new NullabilityInfoContext().Create(parameter);
            return nullability.WriteState == NullabilityState.Nullable;
        }

        return false;
    }

    private static string BuildDescription(string description, bool allowsNull, DefaultValueInfo defaultInfo) {
        var baseDescription = description.Trim();

        var hints = new List<string>(capacity: 3);

        hints.Add(defaultInfo.IsOptional ? "可省略" : "必填");

        if (defaultInfo.IsOptional && !string.IsNullOrWhiteSpace(defaultInfo.DefaultHint)) {
            hints.Add(string.Concat("默认值: ", defaultInfo.DefaultHint));
        }

        if (allowsNull) {
            hints.Add("允许 null");
        }

        if (hints.Count == 0) { return baseDescription; }

        return string.Concat(baseDescription, "；", string.Join("；", hints));
    }

    private static string DescribeDefaultValue(object? value) {
        if (value is null) { return "null"; }

        return value switch {
            string text => $"\"{text}\"",
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null"
        };
    }

    private readonly record struct DefaultValueInfo(ParamDefault? DefaultValue, string? DefaultHint) {
        public bool IsOptional => DefaultValue.HasValue;

        public static DefaultValueInfo None => new(null, null);
    }
}
