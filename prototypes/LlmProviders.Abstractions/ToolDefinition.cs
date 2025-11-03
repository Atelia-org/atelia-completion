using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Atelia.LlmProviders;
public record struct ParamDefault(object? Value);

public sealed class ToolParamSpec {
    public ToolParamSpec(
        string name,
        string description,
        ToolParamValueKind valueKind,
        bool isNullable = false,
        ParamDefault? defaultValue = default,
        string? example = null
    ) {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Parameter name cannot be empty.", nameof(name))
            : name;
        // Type and Nullable
        ValueKind = valueKind;
        IsNullable = isNullable;

        ValidateDefaultCombination(name, ValueKind, IsNullable, defaultValue);

        Default = defaultValue;

        // 这里是统一模板化Description的扩展点，例如类型、nullable、default value等。
        Description = string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("Parameter description cannot be empty.", nameof(description))
            : description;
        Example = example;
    }

    // 最基本的信息
    public string Name { get; }
    public string Description { get; }

    // 类型，包括是否可空
    public ToolParamValueKind ValueKind { get; }
    public bool IsNullable { get; }

    // 默认值
    public ParamDefault? Default { get; }

    public bool TryGetDefaultValue(out object? value) {
        if (Default.HasValue) {
            value = Default.Value.Value;
            return true;
        }
        value = null;
        return false;
    }

    public bool IsOptional => Default.HasValue;
    public bool IsRequired => !Default.HasValue;

    // TODO: 当约束需求明确后，引入通用 IToolParamConstraint（包含枚举、数值范围等实现）。
    public string? Example { get; }

    private static void ValidateDefaultCombination(
        string parameterName,
        ToolParamValueKind valueKind,
        bool isNullable,
        ParamDefault? defaultValue
    ) {
        if (!defaultValue.HasValue) { return; }

        var rawDefault = defaultValue.Value.Value;

        if (rawDefault is null) {
            if (!isNullable) {
                throw new ArgumentException(
                    $"Default value for parameter '{parameterName}' cannot be null when 'isNullable' is false.",
                    nameof(defaultValue)
                );
            }

            return;
        }

        if (!TryValidateValueKindCompatibility(valueKind, rawDefault, out var errorMessage)) { throw new ArgumentException(errorMessage, nameof(defaultValue)); }
    }

    private static bool TryValidateValueKindCompatibility(ToolParamValueKind valueKind, object value, out string errorMessage) {
        switch (valueKind) {
            case ToolParamValueKind.String:
                if (value is string) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamValueKind.Boolean:
                if (value is bool) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamValueKind.Int32:
                if (IsIntegerInRange(value, int.MinValue, int.MaxValue)) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamValueKind.Int64:
                if (IsIntegerInRange(value, long.MinValue, long.MaxValue)) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamValueKind.Float32:
                if (value is float) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamValueKind.Float64:
                if (value is double) {
                    errorMessage = string.Empty;
                    return true;
                }
                break;
            case ToolParamValueKind.Decimal:
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

    private static string GetExpectedTypeName(ToolParamValueKind valueKind) {
        return valueKind switch {
            ToolParamValueKind.String => typeof(string).FullName!,
            ToolParamValueKind.Boolean => typeof(bool).FullName!,
            ToolParamValueKind.Int32 => typeof(int).FullName!,
            ToolParamValueKind.Int64 => typeof(long).FullName!,
            ToolParamValueKind.Float32 => typeof(float).FullName!,
            ToolParamValueKind.Float64 => typeof(double).FullName!,
            ToolParamValueKind.Decimal => typeof(decimal).FullName!,
            _ => valueKind.ToString()
        };
    }
}

public enum ToolParamValueKind {
    String,
    Boolean,
    Int32,
    Int64,
    Float32,
    Float64,
    Decimal
}

public sealed record ToolDefinition(
    string Name,
    string Description,
    ImmutableArray<ToolParamSpec> Parameters
);
