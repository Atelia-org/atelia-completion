using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Utils;

internal static class JsonToolSchemaBuilder {
    public static JsonElement BuildSchema(ToolDefinition definition) {
        if (definition is null) { throw new ArgumentNullException(nameof(definition)); }

        return BuildSchema(definition.InputSchema);
    }

    internal static JsonElement BuildSchema(ToolSchema schema) {
        if (schema is null) { throw new ArgumentNullException(nameof(schema)); }

        return JsonSerializer.SerializeToElement(BuildSchemaNode(schema));
    }

    private static JsonNode BuildSchemaNode(ToolSchema schema) {
        return schema switch {
            ToolSchema.Object objectSchema => BuildObjectSchema(objectSchema),
            ToolSchema.Array arraySchema => BuildArraySchema(arraySchema),
            ToolSchema.Value valueSchema => BuildValueSchema(valueSchema),
            _ => throw new NotSupportedException($"Unsupported tool schema node '{schema.GetType().FullName}'.")
        };
    }

    private static JsonObject BuildObjectSchema(ToolSchema.Object schema) {
        var root = new JsonObject {
            ["type"] = "object",
            ["additionalProperties"] = schema.AdditionalProperties
        };

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in schema.Properties) {
            properties[property.Name] = BuildSchemaNode(property.Schema);

            if (property.IsRequired) {
                required.Add(property.Name);
            }
        }

        if (properties.Count > 0) {
            root["properties"] = properties;
        }

        if (required.Count > 0) {
            root["required"] = required;
        }

        AppendDescription(root, schema.Description);
        AppendExamples(root, schema.Example);
        return root;
    }

    private static JsonObject BuildArraySchema(ToolSchema.Array schema) {
        var node = new JsonObject {
            ["type"] = "array",
            ["items"] = BuildSchemaNode(schema.ItemSchema)
        };

        AppendDescription(node, schema.Description);
        AppendExamples(node, schema.Example);
        return node;
    }

    private static JsonObject BuildValueSchema(ToolSchema.Value parameter) {
        var schema = new JsonObject();

        switch (parameter.ValueKind) {
            case ToolParamType.String:
                schema["type"] = "string";
                break;
            case ToolParamType.Boolean:
                schema["type"] = "boolean";
                break;
            case ToolParamType.Int32:
                schema["type"] = "integer";
                schema["format"] = "int32";
                break;
            case ToolParamType.Int64:
                schema["type"] = "integer";
                schema["format"] = "int64";
                break;
            case ToolParamType.Float32:
                schema["type"] = "number";
                schema["format"] = "float32";
                break;
            case ToolParamType.Float64:
                schema["type"] = "number";
                schema["format"] = "float64";
                break;
            case ToolParamType.Decimal:
                schema["type"] = "number";
                schema["format"] = "decimal";
                break;
            default:
                schema["type"] = "string";
                break;
        }

        AppendDescription(schema, parameter.Description);
        AppendExamples(schema, parameter.Example);
        AppendStringConstraints(schema, parameter);
        AppendNumericConstraints(schema, parameter);

        return schema;
    }

    private static void AppendDescription(JsonObject schema, string? description) {
        if (string.IsNullOrWhiteSpace(description)) { return; }
        schema["description"] = description;
    }

    private static void AppendExamples(JsonObject schema, string? example) {
        if (string.IsNullOrWhiteSpace(example)) { return; }
        schema["examples"] = new JsonArray { JsonValue.Create(example) };
    }

    private static void AppendStringConstraints(JsonObject schema, ToolSchema.Value parameter) {
        if (!parameter.StringEnumValues.IsDefaultOrEmpty) {
            var values = new JsonArray();
            foreach (var value in parameter.StringEnumValues) {
                values.Add(value);
            }
            schema["enum"] = values;
        }

        if (parameter.MinLength.HasValue) {
            schema["minLength"] = parameter.MinLength.Value;
        }

        if (parameter.MaxLength.HasValue) {
            schema["maxLength"] = parameter.MaxLength.Value;
        }

        if (!string.IsNullOrWhiteSpace(parameter.Pattern)) {
            schema["pattern"] = parameter.Pattern;
        }
    }

    private static void AppendNumericConstraints(JsonObject schema, ToolSchema.Value parameter) {
        if (parameter.Minimum is not null) {
            schema["minimum"] = JsonValue.Create(parameter.Minimum);
        }

        if (parameter.Maximum is not null) {
            schema["maximum"] = JsonValue.Create(parameter.Maximum);
        }
    }
}
