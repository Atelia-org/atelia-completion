using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools.Declaration;

namespace Atelia.Completion.Tools;

partial class MethodToolWrapper {
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    static MethodToolWrapper() {
        JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private readonly ToolDefinition _definition;
    private readonly ToolSchema.Object _inputSchema;
    private readonly Type _inputType;
    private readonly Func<object, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> _invoker;

    private MethodToolWrapper(
        ToolDefinition definition,
        ToolSchema.Object inputSchema,
        Type inputType,
        Func<object, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> invoker
    ) {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _inputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));
        _inputType = inputType ?? throw new ArgumentNullException(nameof(inputType));
        _invoker = invoker;
    }

    public async ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
        if (context is null) { throw new ArgumentNullException(nameof(context)); }

        var rawToolCall = context.RawToolCall;
        var parsed = JsonArgumentParser.ParseArguments(_inputSchema, rawToolCall.RawArgumentsJson);
        if (!string.IsNullOrWhiteSpace(parsed.ParseError)) { return CreateParseFailureResult(rawToolCall, parsed); }

        var normalizedRawArguments = NormalizeRawArguments(rawToolCall.RawArgumentsJson);
        object input;

        try {
            input = JsonSerializer.Deserialize(normalizedRawArguments, _inputType, JsonSerializerOptions)
                ?? throw new JsonException($"Tool '{_definition.Name}' deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) {
            return AttachParseWarning(
                new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    BuildDeserializeFailureContent(normalizedRawArguments, ex.Message)
                ),
                parsed.ParseWarning
            );
        }

        var validationErrors = ValidateInputGraph(input);
        if (validationErrors.Count > 0) {
            return AttachParseWarning(
                new ToolExecuteResult(
                    ToolExecutionStatus.Failed,
                    BuildAnnotationFailureContent(rawToolCall, validationErrors)
                ),
                parsed.ParseWarning
            );
        }

        var result = await _invoker(input, context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tool '{_definition.Name}' returned null result.");

        return AttachParseWarning(result, parsed.ParseWarning);
    }
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

        var reflectedDefinition = ReflectedToolDefinitionBuilder.Build(toolAttribute.Name, inputParameter.ParameterType);
        var inputSchema = reflectedDefinition.InputSchema as ToolSchema.Object
            ?? throw new InvalidOperationException($"Tool '{toolAttribute.Name}' must expose an object input schema.");
        var definition = new ToolDefinition(toolAttribute.Name, toolAttribute.Description, inputSchema);
        var invoker = CreateInvoker(method, method.IsStatic ? null : targetInstance, inputParameter.ParameterType);

        return new MethodToolWrapper(
            definition,
            inputSchema,
            inputParameter.ParameterType,
            invoker
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

    private static string NormalizeRawArguments(string rawArguments)
        => string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments;

    private static List<string> ValidateInputGraph(object input) {
        var errors = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        ValidateNode(input, "$", errors, visited);
        return errors;
    }

    private static void ValidateNode(object? value, string path, List<string> errors, HashSet<object> visited) {
        if (value is null) { return; }
        if (value is string) { return; }

        var type = value.GetType();
        if (!type.IsValueType && !visited.Add(value)) { return; }

        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(value);
        _ = Validator.TryValidateObject(value, context, validationResults, validateAllProperties: true);

        foreach (var validationResult in validationResults) {
            var memberNames = validationResult.MemberNames?.ToArray();
            if (memberNames is { Length: > 0 }) {
                foreach (var memberName in memberNames) {
                    var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
                    if (property is not null && !IsSchemaVisibleProperty(property)) { continue; }

                    errors.Add($"{AppendMemberPath(path, ResolveJsonMemberName(type, memberName))}:{validationResult.ErrorMessage}");
                }
            }
            else {
                errors.Add($"{path}:{validationResult.ErrorMessage}");
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
            if (property.GetMethod is not { IsPublic: true, IsStatic: false }) { continue; }
            if (property.GetIndexParameters().Length != 0) { continue; }
            if (!IsSchemaVisibleProperty(property)) { continue; }

            var propertyValue = property.GetValue(value);
            if (propertyValue is null || property.PropertyType == typeof(string)) { continue; }

            var propertyPath = AppendMemberPath(path, ResolveJsonMemberName(property));

            if (propertyValue is IEnumerable enumerable && propertyValue is not IDictionary) {
                var index = 0;
                foreach (var item in enumerable) {
                    ValidateNode(item, $"{propertyPath}[{index++}]", errors, visited);
                }

                continue;
            }

            ValidateNode(propertyValue, propertyPath, errors, visited);
        }
    }

    private static string AppendMemberPath(string path, string memberName)
        => path == "$" ? memberName : $"{path}.{memberName}";

    private static string ResolveJsonMemberName(Type declaringType, string memberName) {
        var property = declaringType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
        return property is null ? memberName : ResolveJsonMemberName(property);
    }

    private static string ResolveJsonMemberName(PropertyInfo property)
        => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;

    private static bool IsSchemaVisibleProperty(PropertyInfo property) {
        var ignoreAttribute = property.GetCustomAttribute<JsonIgnoreAttribute>();
        return ignoreAttribute is null || ignoreAttribute.Condition != JsonIgnoreCondition.Always;
    }

    private static ToolExecuteResult CreateParseFailureResult(RawToolCall request, ToolArgumentParsingResult parsed) {
        var content = "工具参数解析失败。";

        if (!string.IsNullOrWhiteSpace(parsed.ParseError)) {
            content = string.Concat(content, "\n解析错误: ", parsed.ParseError);
        }

        if (!string.IsNullOrWhiteSpace(request.RawArgumentsJson)) {
            content = string.Concat(content, "\nraw_arguments_json: ", request.RawArgumentsJson);
        }

        return new ToolExecuteResult(ToolExecutionStatus.Failed, content);
    }

    private static string BuildDeserializeFailureContent(string rawArgumentsJson, string errorMessage)
        => $"工具参数反序列化失败。\n错误: {errorMessage}\nraw_arguments_json: {rawArgumentsJson}";

    private static string BuildAnnotationFailureContent(RawToolCall request, IReadOnlyList<string> validationErrors) {
        var content = string.Concat("工具参数验证失败。", "\n验证错误: ", string.Join("; ", validationErrors));

        if (!string.IsNullOrWhiteSpace(request.RawArgumentsJson)) {
            content = string.Concat(content, "\nraw_arguments_json: ", request.RawArgumentsJson);
        }

        return content;
    }

    private static ToolExecuteResult AttachParseWarning(ToolExecuteResult result, string? parseWarning) {
        if (string.IsNullOrWhiteSpace(parseWarning)) { return result; }

        var mergedContent = string.Concat(result.Content, "\n[ParseWarning] ", parseWarning);
        return new ToolExecuteResult(result.Status, mergedContent);
    }

}
