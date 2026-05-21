using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Atelia.Completion.Abstractions;

public record struct ParamDefault(object? Value);

internal static class ToolValueValidation {
    internal static void ValidateDefaultCombination(
        string subjectKind,
        string subjectName,
        ToolParamType valueKind,
        bool isNullable,
        ParamDefault? defaultValue
    ) {
        if (!defaultValue.HasValue) { return; }

        var rawDefault = defaultValue.Value.Value;

        if (rawDefault is null) {
            if (!isNullable) {
                throw new ArgumentException(
                    $"Default value for {subjectKind} '{subjectName}' cannot be null when 'isNullable' is false.",
                    nameof(defaultValue)
                );
            }

            return;
        }

        if (!TryValidateValueKindCompatibility(valueKind, rawDefault, out var errorMessage)) {
            throw new ArgumentException(errorMessage, nameof(defaultValue));
        }
    }

    internal static bool TryValidateValueKindCompatibility(ToolParamType valueKind, object value, out string errorMessage) {
        switch (valueKind) {
            case ToolParamType.String:
                if (value is string) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamType.Boolean:
                if (value is bool) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamType.Int32:
                if (IsIntegerInRange(value, int.MinValue, int.MaxValue)) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamType.Int64:
                if (IsIntegerInRange(value, long.MinValue, long.MaxValue)) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamType.Float32:
                if (value is float) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamType.Float64:
                if (value is double) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamType.Decimal:
                if (value is decimal) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            default:
                break;
        }

        errorMessage = $"Default value type '{value.GetType()}' cannot be assigned to parameter kind '{valueKind}'. Expected {GetExpectedTypeName(valueKind)}.";
        return false;
    }

    private static bool IsIntegerInRange(object value, long minValue, long maxValue) {
        return value switch {
            sbyte s => s >= minValue && s <= maxValue,
            byte b => b >= minValue && b <= maxValue,
            short s => s >= minValue && s <= maxValue,
            ushort s => s >= minValue && s <= maxValue,
            int i => i >= minValue && i <= maxValue,
            uint u => u <= maxValue,
            long l => l >= minValue && l <= maxValue,
            ulong ul => minValue <= 0 && maxValue >= 0 && ul <= (ulong)maxValue,
            _ => false
        };
    }

    private static string GetExpectedTypeName(ToolParamType valueKind) {
        return valueKind switch {
            ToolParamType.String => typeof(string).FullName!,
            ToolParamType.Boolean => typeof(bool).FullName!,
            ToolParamType.Int32 => typeof(int).FullName!,
            ToolParamType.Int64 => typeof(long).FullName!,
            ToolParamType.Float32 => typeof(float).FullName!,
            ToolParamType.Float64 => typeof(double).FullName!,
            ToolParamType.Decimal => typeof(decimal).FullName!,
            _ => valueKind.ToString()
        };
    }
}

public enum ToolParamType {
    String,
    Boolean,
    Int32,
    Int64,
    Float32,
    Float64,
    Decimal
}

public abstract record class ToolSchema(string? Description = null, string? Example = null) {
    public sealed record class Property {
        public Property(string name, ToolSchema schema, bool isRequired) {
            Name = string.IsNullOrWhiteSpace(name)
                ? throw new ArgumentException("Property name cannot be empty.", nameof(name))
                : name;
            Schema = schema ?? throw new ArgumentNullException(nameof(schema));
            IsRequired = isRequired;
        }

        public string Name { get; }
        public ToolSchema Schema { get; }
        public bool IsRequired { get; }
    }

    public sealed record class Object : ToolSchema {
        public Object(
            IReadOnlyList<Property>? properties = null,
            bool additionalProperties = false,
            string? description = null,
            string? example = null
        ) : base(description, example) {
            var builder = ImmutableArray.CreateBuilder<Property>();
            var exactNames = new HashSet<string>(StringComparer.Ordinal);
            var caseInsensitiveNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (properties is not null) {
                foreach (var property in properties) {
                    if (!exactNames.Add(property.Name)) {
                        throw new ArgumentException($"Duplicate property '{property.Name}' detected.", nameof(properties));
                    }

                    if (!caseInsensitiveNames.TryAdd(property.Name, property.Name)) {
                        var existingName = caseInsensitiveNames[property.Name];
                        throw new ArgumentException(
                            $"Properties '{existingName}' and '{property.Name}' differ only by case.",
                            nameof(properties)
                        );
                    }

                    builder.Add(property);
                }
            }

            Properties = builder.Count == 0 ? ImmutableArray<Property>.Empty : builder.ToImmutable();
            AdditionalProperties = additionalProperties;
        }

        public ImmutableArray<Property> Properties { get; }
        public bool AdditionalProperties { get; }
    }

    public sealed record class Array : ToolSchema {
        public Array(
            ToolSchema itemSchema,
            bool isNullable = false,
            string? description = null,
            string? example = null
        ) : base(description, example) {
            ItemSchema = itemSchema ?? throw new ArgumentNullException(nameof(itemSchema));
            IsNullable = isNullable;
        }

        public ToolSchema ItemSchema { get; }
        public bool IsNullable { get; }
    }

    public sealed record class Value : ToolSchema {
        public Value(
            ToolParamType valueKind,
            bool isNullable = false,
            ParamDefault? defaultValue = default,
            string? description = null,
            string? example = null,
            IReadOnlyList<string>? stringEnumValues = null,
            int? minLength = null,
            int? maxLength = null,
            string? pattern = null,
            object? minimum = null,
            object? maximum = null
        ) : base(description, example) {
            ToolValueValidation.ValidateDefaultCombination("schema", "schema", valueKind, isNullable, defaultValue);
            ValidateStringConstraints(valueKind, stringEnumValues, minLength, maxLength, pattern);
            ValidateNumericConstraints(valueKind, minimum, maximum);

            ValueKind = valueKind;
            IsNullable = isNullable;
            Default = defaultValue;
            StringEnumValues = stringEnumValues is null
                ? ImmutableArray<string>.Empty
                : ImmutableArray.CreateRange(stringEnumValues);
            MinLength = minLength;
            MaxLength = maxLength;
            Pattern = pattern;
            Minimum = minimum;
            Maximum = maximum;
        }

        public ToolParamType ValueKind { get; }
        public bool IsNullable { get; }
        public ParamDefault? Default { get; }
        public ImmutableArray<string> StringEnumValues { get; }
        public int? MinLength { get; }
        public int? MaxLength { get; }
        public string? Pattern { get; }
        public object? Minimum { get; }
        public object? Maximum { get; }

        private static void ValidateStringConstraints(
            ToolParamType valueKind,
            IReadOnlyList<string>? stringEnumValues,
            int? minLength,
            int? maxLength,
            string? pattern
        ) {
            if (stringEnumValues is null && minLength is null && maxLength is null && pattern is null) { return; }

            if (valueKind != ToolParamType.String) { throw new ArgumentException("String constraints are only valid for string schemas."); }

            if (minLength < 0) { throw new ArgumentOutOfRangeException(nameof(minLength), minLength, "String minLength cannot be negative."); }

            if (maxLength < 0) { throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "String maxLength cannot be negative."); }

            if (minLength.HasValue && maxLength.HasValue && minLength.Value > maxLength.Value) { throw new ArgumentException("String minLength cannot be greater than maxLength."); }

            if (stringEnumValues is not null) {
                foreach (var value in stringEnumValues) {
                    if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException("String enum values cannot contain null or whitespace entries.", nameof(stringEnumValues)); }
                }
            }
        }

        private static void ValidateNumericConstraints(
            ToolParamType valueKind,
            object? minimum,
            object? maximum
        ) {
            if (minimum is null && maximum is null) { return; }

            if (valueKind is not (ToolParamType.Int32 or ToolParamType.Int64 or ToolParamType.Float32 or ToolParamType.Float64 or ToolParamType.Decimal)) { throw new ArgumentException("Numeric constraints are only valid for numeric schemas."); }

            if (minimum is not null && !ToolValueValidation.TryValidateValueKindCompatibility(valueKind, minimum, out var minError)) { throw new ArgumentException($"Invalid minimum constraint: {minError}", nameof(minimum)); }

            if (maximum is not null && !ToolValueValidation.TryValidateValueKindCompatibility(valueKind, maximum, out var maxError)) { throw new ArgumentException($"Invalid maximum constraint: {maxError}", nameof(maximum)); }

            if (minimum is not null && maximum is not null && CompareValues(minimum, maximum, valueKind) > 0) { throw new ArgumentException("Numeric minimum cannot be greater than maximum."); }
        }

        private static int CompareValues(object left, object right, ToolParamType valueKind) {
            return valueKind switch {
                ToolParamType.Int32 => Convert.ToInt32(left).CompareTo(Convert.ToInt32(right)),
                ToolParamType.Int64 => Convert.ToInt64(left).CompareTo(Convert.ToInt64(right)),
                ToolParamType.Float32 => Convert.ToSingle(left).CompareTo(Convert.ToSingle(right)),
                ToolParamType.Float64 => Convert.ToDouble(left).CompareTo(Convert.ToDouble(right)),
                ToolParamType.Decimal => Convert.ToDecimal(left).CompareTo(Convert.ToDecimal(right)),
                _ => throw new ArgumentOutOfRangeException(nameof(valueKind), valueKind, "Value kind does not support numeric comparison.")
            };
        }
    }
}

public sealed record class ToolDefinition {
    public ToolDefinition(string name, string description, ToolSchema inputSchema) {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Tool name cannot be empty.", nameof(name))
            : name;
        Description = string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("Tool description cannot be empty.", nameof(description))
            : description;
        InputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));

        if (InputSchema is not ToolSchema.Object) { throw new ArgumentException("Tool input schema root must be an object.", nameof(inputSchema)); }
    }

    public string Name { get; }
    public string Description { get; }
    public ToolSchema InputSchema { get; }
}
