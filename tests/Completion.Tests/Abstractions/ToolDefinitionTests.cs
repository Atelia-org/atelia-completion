using System.Text.Json;
using Atelia.Completion.Utils;
using Xunit;

namespace Atelia.Completion.Abstractions.Tests;

public sealed class ToolDefinitionTests {
    [Fact]
    public void CreateFlat_PreservesLegacyProviderSchemaShape() {
        var definition = ToolDefinition.CreateFlat(
            name: "get_weather",
            description: "Get weather by city.",
            parameters: [
                new ToolParamSpec("city", "The city name.", ToolParamType.String),
                new ToolParamSpec("days", "Forecast days.", ToolParamType.Int32)
            ]
        );

        Assert.Equal(2, definition.Parameters.Length);

        var schema = JsonToolSchemaBuilder.BuildSchema(definition);

        AssertJsonSemanticallyEqual(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "city": {
                  "type": "string",
                  "description": "The city name."
                },
                "days": {
                  "type": "integer",
                  "format": "int32",
                  "description": "Forecast days."
                }
              },
              "required": ["city", "days"]
            }
            """,
            schema
        );
    }

    [Fact]
    public void NestedInputSchema_BuildsRecursively_WithoutProjectingFlatParameters() {
        var definition = new ToolDefinition(
            name: "search_docs",
            description: "Search docs with nested filters.",
            inputSchema: new ToolSchema.Object(
                properties: [
                    new ToolSchema.Property(
                        "query",
                        new ToolSchema.Object(
                            properties: [
                                new ToolSchema.Property(
                                    "text",
                                    new ToolSchema.Value(ToolParamType.String, description: "Query text."),
                                    isRequired: true
                                )
                            ],
                            description: "Structured query."
                        ),
                        isRequired: true
                    ),
                    new ToolSchema.Property(
                        "tags",
                        new ToolSchema.Array(
                            new ToolSchema.Value(ToolParamType.String, description: "Single tag."),
                            description: "Optional tags."
                        ),
                        isRequired: false
                    )
                ]
            )
        );

        Assert.Empty(definition.Parameters);

        var schema = JsonToolSchemaBuilder.BuildSchema(definition);

        AssertJsonSemanticallyEqual(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "query": {
                  "type": "object",
                  "additionalProperties": false,
                  "description": "Structured query.",
                  "properties": {
                    "text": {
                      "type": "string",
                      "description": "Query text."
                    }
                  },
                  "required": ["text"]
                },
                "tags": {
                  "type": "array",
                  "description": "Optional tags.",
                  "items": {
                    "type": "string",
                    "description": "Single tag."
                  }
                }
              },
              "required": ["query"]
            }
            """,
            schema
        );
    }

    [Fact]
    public void OptionalValueWithoutDefault_DoesNotProjectIncorrectFlatParameters() {
        var definition = new ToolDefinition(
            name: "emit_partial",
            description: "Emit a partially optional payload.",
            inputSchema: new ToolSchema.Object(
                properties: [
                    new ToolSchema.Property(
                        "title",
                        new ToolSchema.Value(ToolParamType.String, description: "Optional title."),
                        isRequired: false
                    )
                ]
            )
        );

        Assert.Empty(definition.Parameters);
    }

    [Fact]
    public void ToolSchemaValue_NonNullableStringWithNullDefault_Throws() {
        var exception = Assert.Throws<ArgumentException>(
            () => new ToolSchema.Value(
                valueKind: ToolParamType.String,
                isNullable: false,
                defaultValue: new ParamDefault(null)
            )
        );

        Assert.Contains("cannot be null", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolSchemaValue_Int32WithNonIntegerMinimum_Throws() {
        var exception = Assert.Throws<ArgumentException>(
            () => new ToolSchema.Value(
                valueKind: ToolParamType.Int32,
                minimum: 3.14
            )
        );

        Assert.Contains("Invalid minimum constraint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolSchemaObject_CaseOnlyDuplicatePropertyNames_Throws() {
        var exception = Assert.Throws<ArgumentException>(
            () => new ToolSchema.Object(
                properties: [
                    new ToolSchema.Property(
                        "City",
                        new ToolSchema.Value(ToolParamType.String),
                        isRequired: true
                    ),
                    new ToolSchema.Property(
                        "city",
                        new ToolSchema.Value(ToolParamType.String),
                        isRequired: false
                    )
                ]
            )
        );

        Assert.Contains("differ only by case", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertJsonSemanticallyEqual(string expectedJson, JsonElement actual) {
        using var expectedDocument = JsonDocument.Parse(expectedJson);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actual),
            $"Expected:\n{expectedDocument.RootElement}\nActual:\n{actual}"
        );
    }
}
