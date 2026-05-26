using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

internal static class ObjectInputToolRuntime {
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly MethodInfo CreateExecutorCoreMethod = typeof(ObjectInputToolRuntime)
        .GetMethod(nameof(CreateExecutorCore), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Failed to locate {nameof(CreateExecutorCore)}.");

    static ObjectInputToolRuntime() {
        JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> CreateExecutor<TInput>(
        ToolDefinition definition,
        ToolSchema.Object inputSchema,
        Func<TInput, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> invokeAsync
    ) where TInput : class {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(invokeAsync);

        return (context, cancellationToken) => ExecuteAsync(definition, inputSchema, context, cancellationToken, invokeAsync);
    }

    public static Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> CreateExecutor(
        Type inputType,
        ToolDefinition definition,
        ToolSchema.Object inputSchema,
        Func<object, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> invokeAsync
    ) {
        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(invokeAsync);

        try {
            return (Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>>)CreateExecutorCoreMethod
                .MakeGenericMethod(inputType)
                .Invoke(null, new object[] { definition, inputSchema, invokeAsync })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> CreateExecutorCore<TInput>(
        ToolDefinition definition,
        ToolSchema.Object inputSchema,
        Func<object, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> invokeAsync
    ) where TInput : class
        => CreateExecutor<TInput>(definition, inputSchema, (input, context, cancellationToken) => invokeAsync(input, context, cancellationToken));

    private static async ValueTask<ToolExecuteResult> ExecuteAsync<TInput>(
        ToolDefinition definition,
        ToolSchema.Object inputSchema,
        ToolExecutionContext context,
        CancellationToken cancellationToken,
        Func<TInput, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> invokeAsync
    ) where TInput : class {
        if (context is null) { throw new ArgumentNullException(nameof(context)); }

        var rawToolCall = context.RawToolCall;
        var parsed = JsonArgumentParser.ParseArguments(inputSchema, rawToolCall.RawArgumentsJson);
        if (!string.IsNullOrWhiteSpace(parsed.ParseError)) { return CreateParseFailureResult(rawToolCall, parsed); }

        var normalizedRawArguments = NormalizeRawArguments(rawToolCall.RawArgumentsJson);
        TInput input;

        try {
            input = JsonSerializer.Deserialize<TInput>(normalizedRawArguments, JsonSerializerOptions)
                ?? throw new JsonException($"Tool '{definition.Name}' deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) {
            return AttachParseWarning(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Failed,
                    BuildDeserializeFailureContent(normalizedRawArguments, ex.Message)
                ),
                parsed.ParseWarning
            );
        }

        var validationErrors = ValidateObjectGraph(input);
        if (validationErrors.Count > 0) {
            return AttachParseWarning(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Failed,
                    BuildAnnotationFailureContent(rawToolCall, validationErrors)
                ),
                parsed.ParseWarning
            );
        }

        var result = await invokeAsync(input, context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tool '{definition.Name}' returned null result.");

        return AttachParseWarning(result, parsed.ParseWarning);
    }

    private static string NormalizeRawArguments(string rawArguments)
        => string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments;

    private static List<string> ValidateObjectGraph(object input) {
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

        return ToolExecuteResult.FromText(ToolExecutionStatus.Failed, content);
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

        var blocks = result.Blocks
            .Concat(new ToolResultBlock[] { new ToolResultBlock.Text(string.Concat("\n[ParseWarning] ", parseWarning)) })
            .ToArray();
        return new ToolExecuteResult(result.Status, blocks);
    }
}
