using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Atelia.Completion.Abstractions;
public record struct ParamDefault(object? Value);

public sealed class ToolParamSpec {
    public ToolParamSpec(
        string name,
        string description,
        ToolParamType valueKind,
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
    /// <summary>
    /// 参数名称，作为工具调用时的参数键。
    /// <para>
    /// <strong>大小写敏感：</strong>调用方传入的参数字典键必须与此名称完全匹配（区分大小写）。
    /// 这是有意为之的设计约束，目的是保持接口契约清晰，避免因忽略大小写而引入的参数名碰撞检测复杂性。
    /// </para>
    /// <para>
    /// <strong>设计决策：</strong>团队内部代码风格统一，参数名拼写准确，大小写敏感能更早暴露调用方的拼写错误。
    /// 若未来确需兼容外部协议的大小写差异或别名，应在具体工具或适配层实现转换逻辑，而非在核心类型层引入通用支持。
    /// </para>
    /// </summary>
    public string Name { get; }
    public string Description { get; }

    // 类型，包括是否可空
    public ToolParamType ValueKind { get; }
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
        ToolParamType valueKind,
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

    private static bool TryValidateValueKindCompatibility(ToolParamType valueKind, object value, out string errorMessage) {
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

public sealed record ToolDefinition(
    string Name,
    string Description,
    ImmutableArray<ToolParamSpec> Parameters
);
