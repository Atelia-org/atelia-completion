using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools.Declaration;

public static class ReflectedToolDefinitionBuilder {
    private static readonly NullabilityInfoContext NullabilityContext = new();

    public static ToolDefinition BuildDefinitionUsingTypeDescription<TInput>(string toolName)
        where TInput : class
        => BuildDefinitionUsingTypeDescription(toolName, typeof(TInput));

    public static ToolDefinition BuildDefinitionUsingTypeDescription(string toolName, Type inputType) {
        ArgumentNullException.ThrowIfNull(inputType);

        var toolDescription = GetRequiredDescription(inputType, "tool definition");
        return BuildDefinition(toolName, toolDescription, inputType);
    }

    public static ToolDefinition BuildDefinition<TInput>(string toolName, string toolDescription)
        where TInput : class
        => BuildDefinition(toolName, toolDescription, typeof(TInput));

    public static ToolDefinition BuildDefinition(string toolName, string toolDescription, Type inputType) {
        var inputSchema = BuildInputObjectSchema(inputType);
        return new ToolDefinition(toolName, toolDescription, inputSchema);
    }

    public static ToolSchema.Object BuildInputObjectSchema<TInput>()
        where TInput : class
        => BuildInputObjectSchema(typeof(TInput));

    public static ToolSchema.Object BuildInputObjectSchema(Type inputType) {
        ArgumentNullException.ThrowIfNull(inputType);
        return BuildRootSchema(inputType, new HashSet<Type>());
    }

    private static ToolSchema.Object BuildRootSchema(Type type, HashSet<Type> typePath) {
        EnsureSupportedObjectType(type, "root tool declaration");
        return BuildObjectSchema(type, schemaDescription: null, typePath);
    }

    private static ToolSchema.Object BuildObjectSchema(Type type, string? schemaDescription, HashSet<Type> typePath) {
        EnsureSupportedObjectType(type, "object schema");

        if (!typePath.Add(type)) { throw new NotSupportedException($"Cycle detected while reflecting tool declaration type '{type.FullName}'."); }

        try {
            var properties = ImmutableArray.CreateBuilder<ToolSchema.Property>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
                if (property.GetMethod is not { IsPublic: true, IsStatic: false }) { continue; }
                if (property.GetIndexParameters().Length != 0) { continue; }

                if (TryShouldIgnore(property, out var ignoreError)) {
                    if (ignoreError is not null) { throw ignoreError; }

                    continue;
                }

                var propertyName = ResolvePropertyName(property);
                if (!names.Add(propertyName)) {
                    throw new InvalidOperationException(
                        $"Type '{type.FullName}' contains duplicate JSON property names that differ only by case: '{propertyName}'."
                    );
                }

                var propertyDescription = ResolvePropertyDescription(property);
                var nullability = NullabilityContext.Create(property);
                var isRequired = property.GetCustomAttribute<RequiredAttribute>() is not null || !AllowsNull(property.PropertyType, nullability);
                var propertySchema = BuildSchemaForProperty(property, propertyDescription, nullability, typePath);
                properties.Add(new ToolSchema.Property(propertyName, propertySchema, isRequired));
            }

            return new ToolSchema.Object(properties.ToImmutable(), description: schemaDescription);
        }
        finally {
            _ = typePath.Remove(type);
        }
    }

    private static ToolSchema BuildSchemaForProperty(
        PropertyInfo property,
        string propertyDescription,
        NullabilityInfo nullability,
        HashSet<Type> typePath
    ) {
        var propertyType = property.PropertyType;
        var underlyingNullableType = Nullable.GetUnderlyingType(propertyType);
        var effectiveType = underlyingNullableType ?? propertyType;

        if (TryBuildScalarSchema(effectiveType, propertyDescription, property, out var scalarSchema)) { return scalarSchema; }

        if (TryGetSupportedCollectionElementType(effectiveType, out var elementType)) {
            if (AllowsNull(propertyType, nullability)) { throw new NotSupportedException($"Nullable array/object property '{property.DeclaringType?.FullName}.{property.Name}' is not supported."); }

            var elementNullability = GetCollectionElementNullability(nullability);
            if (elementNullability is not null && AllowsNull(elementType, elementNullability)) { throw new NotSupportedException($"Nullable collection elements are not supported for property '{property.DeclaringType?.FullName}.{property.Name}'."); }

            var itemSchema = BuildSchemaForNestedType(
                elementType,
                GetOptionalDescription(elementType),
                elementNullability,
                property,
                typePath
            );

            return new ToolSchema.Array(itemSchema, description: propertyDescription);
        }

        if (effectiveType.IsValueType) { throw new NotSupportedException($"Unsupported tool declaration type '{effectiveType.FullName}' on property '{property.DeclaringType?.FullName}.{property.Name}'."); }

        if (AllowsNull(propertyType, nullability)) { throw new NotSupportedException($"Nullable array/object property '{property.DeclaringType?.FullName}.{property.Name}' is not supported."); }

        return BuildObjectSchema(effectiveType, propertyDescription, typePath);
    }

    private static ToolSchema BuildSchemaForNestedType(
        Type type,
        string? schemaDescription,
        NullabilityInfo? nullability,
        PropertyInfo declaringProperty,
        HashSet<Type> typePath
    ) {
        var underlyingNullableType = Nullable.GetUnderlyingType(type);
        var effectiveType = underlyingNullableType ?? type;

        if (TryBuildScalarSchema(effectiveType, schemaDescription, declaringProperty, out var scalarSchema)) { return scalarSchema; }

        if (TryGetSupportedCollectionElementType(effectiveType, out _)) {
            throw new NotSupportedException(
                $"Nested collection type '{effectiveType.FullName}' is not supported for property '{declaringProperty.DeclaringType?.FullName}.{declaringProperty.Name}'."
            );
        }

        if (effectiveType.IsValueType) {
            throw new NotSupportedException(
                $"Unsupported nested tool declaration type '{effectiveType.FullName}' on property '{declaringProperty.DeclaringType?.FullName}.{declaringProperty.Name}'."
            );
        }

        if (nullability is not null && AllowsNull(type, nullability)) { throw new NotSupportedException($"Nullable array/object property '{declaringProperty.DeclaringType?.FullName}.{declaringProperty.Name}' is not supported."); }

        return BuildObjectSchema(effectiveType, schemaDescription, typePath);
    }

    private static bool TryBuildScalarSchema(
        Type type,
        string? description,
        PropertyInfo property,
        out ToolSchema.Value schema
    ) {
        if (type == typeof(string)) {
            schema = new ToolSchema.Value(
                ToolParamType.String,
                description: description,
                stringEnumValues: null,
                minLength: GetMinLength(property),
                maxLength: GetMaxLength(property),
                pattern: property.GetCustomAttribute<RegularExpressionAttribute>()?.Pattern
            );
            return true;
        }

        if (type == typeof(bool)) {
            schema = new ToolSchema.Value(ToolParamType.Boolean, description: description);
            return true;
        }

        if (type == typeof(int)) {
            schema = BuildIntegerSchema(property, description, ToolParamType.Int32, Convert.ToInt32);
            return true;
        }

        if (type == typeof(long)) {
            schema = BuildIntegerSchema(property, description, ToolParamType.Int64, Convert.ToInt64);
            return true;
        }

        if (type == typeof(double)) {
            var range = property.GetCustomAttribute<RangeAttribute>();
            schema = new ToolSchema.Value(
                ToolParamType.Float64,
                description: description,
                minimum: range is null ? null : Convert.ToDouble(range.Minimum),
                maximum: range is null ? null : Convert.ToDouble(range.Maximum)
            );
            return true;
        }

        if (type.IsEnum) {
            if (type.GetCustomAttribute<FlagsAttribute>() is not null) { throw new NotSupportedException($"Flags enum '{type.FullName}' is not supported for tool declarations."); }

            schema = new ToolSchema.Value(
                ToolParamType.String,
                description: description,
                stringEnumValues: Enum.GetNames(type)
            );
            return true;
        }

        schema = default!;
        return false;
    }

    private static ToolSchema.Value BuildIntegerSchema<T>(
        PropertyInfo property,
        string? description,
        ToolParamType valueKind,
        Func<object, T> convert
    ) where T : notnull {
        var range = property.GetCustomAttribute<RangeAttribute>();

        return new ToolSchema.Value(
            valueKind,
            description: description,
            minimum: range is null ? null : convert(range.Minimum),
            maximum: range is null ? null : convert(range.Maximum)
        );
    }

    private static bool TryShouldIgnore(PropertyInfo property, out Exception? error) {
        var ignoreAttribute = property.GetCustomAttribute<JsonIgnoreAttribute>();
        if (ignoreAttribute is null) {
            error = null;
            return false;
        }

        if (ignoreAttribute.Condition != JsonIgnoreCondition.Always) {
            error = new NotSupportedException(
                $"Property '{property.DeclaringType?.FullName}.{property.Name}' uses unsupported JsonIgnoreCondition '{ignoreAttribute.Condition}'."
            );
            return true;
        }

        error = null;
        return true;
    }

    private static string ResolvePropertyName(PropertyInfo property) {
        var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
        if (string.IsNullOrWhiteSpace(name)) { throw new InvalidOperationException($"Property '{property.DeclaringType?.FullName}.{property.Name}' resolved to an empty JSON property name."); }

        return name;
    }

    private static string ResolvePropertyDescription(PropertyInfo property)
        => GetOptionalDescription(property) ?? property.Name;

    private static string GetRequiredDescription(MemberInfo member, string targetName) {
        var description = GetOptionalDescription(member);
        if (description is null) { throw new InvalidOperationException($"Type '{member.DeclaringType?.FullName ?? member.Name}' is missing [Description] for {targetName}."); }

        return description;
    }

    private static string? GetOptionalDescription(MemberInfo member) {
        var description = member.GetCustomAttribute<DescriptionAttribute>()?.Description;
        return string.IsNullOrWhiteSpace(description) ? null : description;
    }

    private static int? GetMinLength(PropertyInfo property) {
        var stringLength = property.GetCustomAttribute<StringLengthAttribute>();
        return stringLength is { MinimumLength: > 0 } ? stringLength.MinimumLength : null;
    }

    private static int? GetMaxLength(PropertyInfo property)
        => property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength;

    private static void EnsureSupportedObjectType(Type type, string role) {
        if (!type.IsClass || type == typeof(string) || type.IsAbstract || type.IsInterface) { throw new NotSupportedException($"Only concrete class / record class declarations are supported for {role}. Actual type: '{type.FullName}'."); }
    }

    private static bool AllowsNull(Type type, NullabilityInfo nullability) {
        if (Nullable.GetUnderlyingType(type) is not null) { return true; }
        if (!type.IsValueType) { return nullability.ReadState == NullabilityState.Nullable; }
        return false;
    }

    private static bool TryGetSupportedCollectionElementType(Type type, out Type elementType) {
        if (type.IsArray) {
            elementType = type.GetElementType()!;
            return true;
        }

        if (!type.IsGenericType) {
            elementType = null!;
            return false;
        }

        var genericType = type.GetGenericTypeDefinition();
        if (genericType == typeof(List<>) || genericType == typeof(IReadOnlyList<>)) {
            elementType = type.GenericTypeArguments[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    private static NullabilityInfo? GetCollectionElementNullability(NullabilityInfo nullability) {
        if (nullability.ElementType is not null) { return nullability.ElementType; }
        if (nullability.GenericTypeArguments.Length > 0) { return nullability.GenericTypeArguments[0]; }
        return null;
    }
}
