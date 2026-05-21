using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Declaration;

namespace Atelia.Completion.Tools;

partial class ArtifactToolWrapper<T> {
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    static ArtifactToolWrapper() {
        JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private readonly ToolDefinition _definition;
    private readonly ToolSchema.Object _inputSchema;
    private readonly ArtifactHandler<T> _handler;
    private readonly object _sequenceGate = new();
    private int _nextSequence;

    private ArtifactToolWrapper(
        ToolDefinition definition,
        ToolSchema.Object inputSchema,
        ArtifactHandler<T> handler
    ) {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _inputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Visible = true;
    }

    private static partial ArtifactToolWrapper<T> Bind(string toolName, ArtifactHandler<T> handler) {
        if (string.IsNullOrWhiteSpace(toolName)) { throw new ArgumentException("Tool name cannot be null or whitespace.", nameof(toolName)); }
        ArgumentNullException.ThrowIfNull(handler);

        var definition = ReflectedToolDefinitionBuilder.Build(toolName, typeof(T));
        var inputSchema = definition.InputSchema as ToolSchema.Object
            ?? throw new InvalidOperationException($"Tool '{toolName}' must expose an object input schema.");

        return new ArtifactToolWrapper<T>(definition, inputSchema, handler);
    }

    public partial ValueTask<ToolExecuteResult> ExecuteAsync(RawToolCall request, CancellationToken cancellationToken) {
        if (request is null) { throw new ArgumentNullException(nameof(request)); }

        var parsed = JsonArgumentParser.ParseArguments(_inputSchema, request.RawArgumentsJson);
        if (!string.IsNullOrWhiteSpace(parsed.ParseError)) {
            return ValueTask.FromResult(CreateParseFailureResult(request, parsed));
        }

        var normalizedRawArguments = NormalizeRawArguments(request.RawArgumentsJson);
        T artifact;

        try {
            artifact = JsonSerializer.Deserialize<T>(normalizedRawArguments, JsonSerializerOptions)
                ?? throw new JsonException($"Tool '{_definition.Name}' deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) {
            return ValueTask.FromResult(
                AttachParseWarning(
                    new ToolExecuteResult(
                        ToolExecutionStatus.Failed,
                        BuildDeserializeFailureContent(normalizedRawArguments, ex.Message)
                    ),
                    parsed.ParseWarning
                )
            );
        }

        var validationErrors = ValidateArtifactGraph(artifact);
        if (validationErrors.Count > 0) {
            return ValueTask.FromResult(
                AttachParseWarning(
                    new ToolExecuteResult(
                        ToolExecutionStatus.Failed,
                        BuildAnnotationFailureContent(request, validationErrors)
                    ),
                    parsed.ParseWarning
                )
            );
        }

        int sequence;
        ValidateResult handlerResult;

        lock (_sequenceGate) {
            sequence = _nextSequence + 1;
            handlerResult = _handler(sequence, artifact);
            if (handlerResult.IsValid) {
                _nextSequence = sequence;
            }
        }

        var result = handlerResult.IsValid
            ? new ToolExecuteResult(
                ToolExecutionStatus.Success,
                string.IsNullOrWhiteSpace(handlerResult.message)
                    ? $"产物已接收。sequence={sequence}"
                    : handlerResult.message
            )
            : new ToolExecuteResult(
                ToolExecutionStatus.Failed,
                string.IsNullOrWhiteSpace(handlerResult.message)
                    ? "产物校验失败。"
                    : $"产物校验失败。\n原因: {handlerResult.message}"
            );

        return ValueTask.FromResult(AttachParseWarning(result, parsed.ParseWarning));
    }

    private static string NormalizeRawArguments(string rawArguments)
        => string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments;

    private static List<string> ValidateArtifactGraph(T artifact) {
        var errors = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        ValidateNode(artifact, "$", errors, visited);
        return errors;
    }

    private static void ValidateNode(object? value, string path, List<string> errors, HashSet<object> visited) {
        if (value is null) { return; }
        if (value is string) { return; }

        var type = value.GetType();
        if (!type.IsValueType && !visited.Add(value)) { return; }

        var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
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
        => property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name ?? property.Name;

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
