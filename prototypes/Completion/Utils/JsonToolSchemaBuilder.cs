using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Utils;

internal static class JsonToolSchemaBuilder {
    public static JsonElement BuildSchema(ToolDefinition definition) {
        if (definition is null) { throw new ArgumentNullException(nameof(definition)); }

        var root = new JsonObject {
            ["type"] = "object",
            ["additionalProperties"] = false
        };

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in definition.Parameters) {
            var schema = BuildParameterSchema(parameter);
            properties[parameter.Name] = schema;

            if (parameter.IsRequired) {
                required.Add(parameter.Name);
            }
        }

        if (properties.Count > 0) {
            root["properties"] = properties;
        }

        if (required.Count > 0) {
            root["required"] = required;
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonNode BuildParameterSchema(ToolParamSpec parameter) {
        var schema = new JsonObject();

        switch (parameter.ValueKind) {
            case ToolParamValueKind.String:
                schema["type"] = "string";
                break;
            case ToolParamValueKind.Boolean:
                schema["type"] = "boolean";
                break;
            case ToolParamValueKind.Int32:
                schema["type"] = "integer";
                schema["format"] = "int32";
                break;
            case ToolParamValueKind.Int64:
                schema["type"] = "integer";
                schema["format"] = "int64";
                break;
            case ToolParamValueKind.Float32:
                schema["type"] = "number";
                schema["format"] = "float32";
                break;
            case ToolParamValueKind.Float64:
                schema["type"] = "number";
                schema["format"] = "float64";
                break;
            case ToolParamValueKind.Decimal:
                schema["type"] = "number";
                schema["format"] = "decimal";
                break;
            default:
                schema["type"] = "string";
                break;
        }

        AppendDescription(schema, parameter.Description);
        AppendExamples(schema, parameter.Example);
        // TODO: 在引入通用参数约束体系后，将枚举等约束信息映射到 schema。

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
}
