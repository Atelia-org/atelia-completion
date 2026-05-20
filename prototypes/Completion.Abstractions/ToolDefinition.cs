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
    /// 大小写敏感：调用方传入的参数字典键必须与此名称完全匹配（区分大小写）。
    /// 这是有意为之的设计约束，目的是保持接口契约清晰，避免因忽略大小写而引入的参数名碰撞检测复杂性。
    /// 设计决策：团队内部代码风格统一，参数名拼写准确，大小写敏感能更早暴露调用方的拼写错误。
    /// 若未来确需兼容外部协议的大小写差异或别名，应在具体工具或适配层实现转换逻辑，而非在核心类型层引入通用支持。
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

    internal static void ValidateDefaultCombination(
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
            var names = new HashSet<string>(StringComparer.Ordinal);

            if (properties is not null) {
                foreach (var property in properties) {
                    if (!names.Add(property.Name)) {
                        throw new ArgumentException($"Duplicate property '{property.Name}' detected.", nameof(properties));
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
            string? example = null
        ) : base(description, example) {
            ToolParamSpec.ValidateDefaultCombination("schema", valueKind, isNullable, defaultValue);

            ValueKind = valueKind;
            IsNullable = isNullable;
            Default = defaultValue;
        }

        public ToolParamType ValueKind { get; }
        public bool IsNullable { get; }
        public ParamDefault? Default { get; }
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

        if (InputSchema is not ToolSchema.Object objectSchema) {
            throw new ArgumentException("Tool input schema root must be an object.", nameof(inputSchema));
        }

        Parameters = TryProjectFlatParameters(objectSchema);
    }

    public string Name { get; }
    public string Description { get; }
    public ToolSchema InputSchema { get; }

    // Compatibility projection for remaining flat-schema display paths.
    public ImmutableArray<ToolParamSpec> Parameters { get; }

    public static ToolDefinition CreateFlat(
        string name,
        string description,
        IReadOnlyList<ToolParamSpec>? parameters = null
    ) {
        var normalizedParameters = NormalizeParameters(parameters);
        var inputSchema = BuildFlatInputSchema(normalizedParameters);
        return new ToolDefinition(name, description, inputSchema);
    }

    private static ImmutableArray<ToolParamSpec> NormalizeParameters(IReadOnlyList<ToolParamSpec>? parameters) {
        if (parameters is null || parameters.Count == 0) {
            return ImmutableArray<ToolParamSpec>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<ToolParamSpec>(parameters.Count);
        foreach (var parameter in parameters) {
            builder.Add(parameter ?? throw new ArgumentException("Tool parameter cannot be null.", nameof(parameters)));
        }

        return builder.ToImmutable();
    }

    private static ToolSchema.Object BuildFlatInputSchema(ImmutableArray<ToolParamSpec> parameters) {
        if (parameters.IsDefaultOrEmpty) {
            return new ToolSchema.Object();
        }

        var properties = new ToolSchema.Property[parameters.Length];
        for (var i = 0; i < parameters.Length; i++) {
            var parameter = parameters[i];
            properties[i] = new ToolSchema.Property(
                parameter.Name,
                new ToolSchema.Value(
                    parameter.ValueKind,
                    parameter.IsNullable,
                    parameter.Default,
                    parameter.Description,
                    parameter.Example
                ),
                parameter.IsRequired
            );
        }

        return new ToolSchema.Object(properties);
    }

    private static ImmutableArray<ToolParamSpec> TryProjectFlatParameters(ToolSchema.Object schema) {
        if (schema.AdditionalProperties || schema.Properties.IsDefaultOrEmpty) {
            return ImmutableArray<ToolParamSpec>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<ToolParamSpec>(schema.Properties.Length);
        foreach (var property in schema.Properties) {
            if (property.Schema is not ToolSchema.Value valueSchema || string.IsNullOrWhiteSpace(valueSchema.Description)) {
                return ImmutableArray<ToolParamSpec>.Empty;
            }

            var defaultValue = property.IsRequired ? default(ParamDefault?) : valueSchema.Default;
            builder.Add(
                new ToolParamSpec(
                    property.Name,
                    valueSchema.Description,
                    valueSchema.ValueKind,
                    valueSchema.IsNullable,
                    defaultValue,
                    valueSchema.Example
                )
            );
        }

        return builder.ToImmutable();
    }
}
