using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Anthropic.Tests;

public sealed class AnthropicToolSchemaProjectionTests {
    [Fact]
    public void ConvertToApiRequest_ProjectsRecursiveToolSchemaIntoInputSchema() {
        var request = new CompletionRequest(
            ModelId: "claude-3-7-sonnet",
            SystemPrompt: string.Empty,
            Context: [new ObservationMessage("Search the docs.")],
            Tools: ImmutableArray.Create(CreateRecursiveToolDefinition())
        );

        var apiRequest = AnthropicMessageConverter.ConvertToApiRequest(request);

        var tool = Assert.Single(apiRequest.Tools!);
        Assert.Equal("search_docs", tool.Name);

        AssertJsonSemanticallyEqual(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "query": {
                  "type": "string",
                  "description": "Query text.",
                  "minLength": 3,
                  "maxLength": 50,
                  "pattern": "^[a-z ]+$"
                },
                "filters": {
                  "type": "object",
                  "additionalProperties": false,
                  "description": "Structured filters.",
                  "properties": {
                    "mode": {
                      "type": "string",
                      "description": "Search mode.",
                      "enum": ["Exact", "Fuzzy"]
                    },
                    "clauses": {
                      "type": "array",
                      "description": "Filter clauses.",
                      "items": {
                        "type": "object",
                        "additionalProperties": false,
                        "description": "Single filter clause.",
                        "properties": {
                          "field": {
                            "type": "string",
                            "description": "Target field.",
                            "enum": ["title", "body"]
                          },
                          "boost": {
                            "type": "number",
                            "format": "float64",
                            "description": "Boost weight.",
                            "minimum": 0.1,
                            "maximum": 2
                          },
                          "terms": {
                            "type": "array",
                            "description": "Terms to match.",
                            "items": {
                              "type": "string",
                              "description": "Single term.",
                              "minLength": 2,
                              "maxLength": 20
                            }
                          }
                        },
                        "required": ["field", "boost", "terms"]
                      }
                    }
                  },
                  "required": ["mode", "clauses"]
                }
              },
              "required": ["query", "filters"]
            }
            """,
            tool.InputSchema
        );
    }

    private static ToolDefinition CreateRecursiveToolDefinition() {
        return new ToolDefinition(
            name: "search_docs",
            description: "Search docs with recursive filters.",
            inputSchema: new ToolSchema.Object(
                properties: [
                    new ToolSchema.Property(
                        "query",
                        new ToolSchema.Value(
                            ToolParamType.String,
                            description: "Query text.",
                            minLength: 3,
                            maxLength: 50,
                            pattern: "^[a-z ]+$"
                        ),
                        isRequired: true
                    ),
                    new ToolSchema.Property(
                        "filters",
                        new ToolSchema.Object(
                            properties: [
                                new ToolSchema.Property(
                                    "mode",
                                    new ToolSchema.Value(
                                        ToolParamType.String,
                                        description: "Search mode.",
                                        stringEnumValues: ["Exact", "Fuzzy"]
                                    ),
                                    isRequired: true
                                ),
                                new ToolSchema.Property(
                                    "clauses",
                                    new ToolSchema.Array(
                                        new ToolSchema.Object(
                                            properties: [
                                                new ToolSchema.Property(
                                                    "field",
                                                    new ToolSchema.Value(
                                                        ToolParamType.String,
                                                        description: "Target field.",
                                                        stringEnumValues: ["title", "body"]
                                                    ),
                                                    isRequired: true
                                                ),
                                                new ToolSchema.Property(
                                                    "boost",
                                                    new ToolSchema.Value(
                                                        ToolParamType.Float64,
                                                        description: "Boost weight.",
                                                        minimum: 0.1d,
                                                        maximum: 2d
                                                    ),
                                                    isRequired: true
                                                ),
                                                new ToolSchema.Property(
                                                    "terms",
                                                    new ToolSchema.Array(
                                                        new ToolSchema.Value(
                                                            ToolParamType.String,
                                                            description: "Single term.",
                                                            minLength: 2,
                                                            maxLength: 20
                                                        ),
                                                        description: "Terms to match."
                                                    ),
                                                    isRequired: true
                                                )
                                            ],
                                            description: "Single filter clause."
                                        ),
                                        description: "Filter clauses."
                                    ),
                                    isRequired: true
                                )
                            ],
                            description: "Structured filters."
                        ),
                        isRequired: true
                    )
                ]
            )
        );
    }

    private static void AssertJsonSemanticallyEqual(string expectedJson, JsonElement actual) {
        using var expectedDocument = JsonDocument.Parse(expectedJson);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actual),
            $"Expected:\n{expectedDocument.RootElement}\nActual:\n{actual}"
        );
    }
}
