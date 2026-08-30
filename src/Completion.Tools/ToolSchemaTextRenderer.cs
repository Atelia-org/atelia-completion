using System.Globalization;
using System.Text;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

/// <summary>
/// 将 <see cref="ToolDefinition"/> / <see cref="ToolSchema"/> 渲染为只读文本，供 prompt/debug 展示使用。
/// </summary>
public static class ToolSchemaTextRenderer {
    public static string RenderDefinitions(IEnumerable<ToolDefinition> definitions) {
        ArgumentNullException.ThrowIfNull(definitions);

        var sb = new StringBuilder();
        foreach (var definition in definitions) {
            ArgumentNullException.ThrowIfNull(definition);
            AppendDefinition(sb, definition);
        }

        return sb.ToString();
    }

    public static string RenderDefinition(ToolDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var sb = new StringBuilder();
        AppendDefinition(sb, definition);
        return sb.ToString();
    }

    private static void AppendDefinition(StringBuilder sb, ToolDefinition definition) {
        sb.Append("- ").Append(definition.Name).Append(": ").AppendLine(definition.Description);
        AppendObjectProperties(sb, (ToolSchema.Object)definition.InputSchema, indentLevel: 1);
    }

    private static void AppendObjectProperties(StringBuilder sb, ToolSchema.Object schema, int indentLevel) {
        foreach (var property in schema.Properties) {
            AppendSchemaNode(sb, property.Name, property.Schema, property.IsRequired, indentLevel);
        }

        if (!schema.AdditionalProperties) { return; }

        AppendIndent(sb, indentLevel);
        sb.AppendLine("- <additionalProperties> (object): 允许额外字段");
    }

    private static void AppendSchemaNode(
        StringBuilder sb,
        string name,
        ToolSchema schema,
        bool? isRequired,
        int indentLevel
    ) {
        AppendIndent(sb, indentLevel);
        sb.Append("- ").Append(name);

        var annotations = BuildAnnotations(schema, isRequired);
        if (annotations.Count > 0) {
            sb.Append(" (").Append(string.Join(", ", annotations)).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(schema.Description)) {
            sb.Append(": ").Append(schema.Description);
        }

        sb.AppendLine();

        switch (schema) {
            case ToolSchema.Object objectSchema:
                AppendObjectProperties(sb, objectSchema, indentLevel + 1);
                break;
            case ToolSchema.Array arraySchema when ShouldExpandArrayItem(arraySchema.ItemSchema):
                AppendSchemaNode(sb, "item", arraySchema.ItemSchema, isRequired: null, indentLevel + 1);
                break;
        }
    }

    private static List<string> BuildAnnotations(ToolSchema schema, bool? isRequired) {
        var annotations = new List<string>(capacity: 6);
        if (isRequired.HasValue) {
            annotations.Add(isRequired.Value ? "必填" : "可省略");
        }

        annotations.Add(DescribeType(schema));

        switch (schema) {
            case ToolSchema.Value valueSchema:
                if (valueSchema.Default.HasValue) {
                    annotations.Add(string.Concat("默认值: ", FormatValue(valueSchema.Default.Value.Value)));
                }

                if (valueSchema.IsNullable) {
                    annotations.Add("允许 null");
                }

                if (valueSchema.StringEnumValues.Length > 0) {
                    annotations.Add(string.Concat("枚举: ", string.Join(" | ", valueSchema.StringEnumValues)));
                }

                if (valueSchema.MinLength.HasValue) {
                    annotations.Add(string.Concat("minLength: ", valueSchema.MinLength.Value.ToString(CultureInfo.InvariantCulture)));
                }

                if (valueSchema.MaxLength.HasValue) {
                    annotations.Add(string.Concat("maxLength: ", valueSchema.MaxLength.Value.ToString(CultureInfo.InvariantCulture)));
                }

                if (!string.IsNullOrWhiteSpace(valueSchema.Pattern)) {
                    annotations.Add(string.Concat("pattern: ", valueSchema.Pattern));
                }

                if (valueSchema.Minimum is not null) {
                    annotations.Add(string.Concat("minimum: ", FormatValue(valueSchema.Minimum)));
                }

                if (valueSchema.Maximum is not null) {
                    annotations.Add(string.Concat("maximum: ", FormatValue(valueSchema.Maximum)));
                }
                break;
            case ToolSchema.Array arraySchema when arraySchema.IsNullable:
                annotations.Add("允许 null");
                break;
        }

        return annotations;
    }

    private static string DescribeType(ToolSchema schema) {
        return schema switch {
            ToolSchema.Object => "object",
            ToolSchema.Array arraySchema => string.Concat("array<", DescribeType(arraySchema.ItemSchema), ">"),
            ToolSchema.Value valueSchema => DescribeValueType(valueSchema.ValueKind),
            _ => "unknown"
        };
    }

    private static bool ShouldExpandArrayItem(ToolSchema schema)
        => schema is ToolSchema.Object or ToolSchema.Array;

    private static string DescribeValueType(ToolParamType valueKind) {
        return valueKind switch {
            ToolParamType.String => "string",
            ToolParamType.Boolean => "boolean",
            ToolParamType.Int32 => "int32",
            ToolParamType.Int64 => "int64",
            ToolParamType.Float32 => "float32",
            ToolParamType.Float64 => "float64",
            ToolParamType.Decimal => "decimal",
            _ => valueKind.ToString()
        };
    }

    private static string FormatValue(object? value) {
        if (value is null) { return "null"; }

        return value switch {
            string text => string.Concat("\"", text, "\""),
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null"
        };
    }

    private static void AppendIndent(StringBuilder sb, int indentLevel) {
        sb.Append(' ', Math.Max(indentLevel, 0) * 2);
    }
}
