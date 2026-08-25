using System.Collections.Immutable;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesToolSchemaProjectionTests {
    [Fact]
    public void ConvertToApiRequest_ProjectsRecursiveToolSchemaIntoStrictFunctionTools() {
        var request = new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray.Create(CreateRecursiveToolDefinition())),
                [new ObservationMessage("Search the docs.")]
            ),
            tailMessages: []
        );

        var apiRequest = OpenAIResponsesMessageConverter.ConvertToApiRequest(
            request,
            new OpenAIResponsesClientOptions {
                IncludeEncryptedReasoning = false
            }
        );

        var tool = Assert.Single(apiRequest.Tools!);
        Assert.Equal("function", tool.Type);
        Assert.Equal("search_docs", tool.Name);
        Assert.Equal("Search docs with recursive filters.", tool.Description);
        Assert.True(tool.Strict);
        Assert.Null(apiRequest.Include);

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
                  "type": ["object", "null"],
                  "additionalProperties": false,
                  "description": "Structured filters.",
                  "properties": {
                    "mode": {
                      "type": ["string", "null"],
                      "description": "Search mode.",
                      "enum": ["Exact", "Fuzzy", null]
                    },
                    "clauses": {
                      "type": ["array", "null"],
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
            tool.Parameters
        );
    }

    [Theory]
    [InlineData("root-optional")]
    [InlineData("nested-object-optional")]
    [InlineData("array-item-optional")]
    [InlineData("nested-additional-properties")]
    public void ConvertToApiRequest_PreservesSchemaButDisablesStrictWhenAnyNodeIsIncompatible(
        string incompatibleNode
    ) {
        ToolSchema nestedObject = new ToolSchema.Object(
            properties: [
                new ToolSchema.Property(
                    "value",
                    new ToolSchema.Value(ToolParamType.String),
                    isRequired: incompatibleNode is not "nested-object-optional"
                )
            ],
            additionalProperties: incompatibleNode is "nested-additional-properties"
        );
        var properties = new List<ToolSchema.Property> {
            new(
                "direct",
                new ToolSchema.Value(ToolParamType.String),
                isRequired: incompatibleNode is not "root-optional"
            ),
            new("nested", nestedObject, isRequired: true),
            new(
                "items",
                new ToolSchema.Array(
                    new ToolSchema.Object([
                        new ToolSchema.Property(
                            "itemValue",
                            new ToolSchema.Value(ToolParamType.String),
                            isRequired: incompatibleNode is not "array-item-optional"
                        )
                    ])
                ),
                isRequired: true
            )
        };
        ToolDefinition definition = new(
            "recursive_tool",
            "Exercise recursive strict compatibility.",
            new ToolSchema.Object(properties)
        );

        OpenAIResponsesApiRequest apiRequest = Convert(definition);

        OpenAIResponsesTool tool = Assert.Single(apiRequest.Tools!);
        Assert.False(tool.Strict);
        JsonElement root = tool.Parameters;
        Assert.Equal(
            incompatibleNode is not "root-optional",
            root.GetProperty("required")
                .EnumerateArray()
                .Any(static item => item.GetString() == "direct")
        );
        JsonElement nested = root.GetProperty("properties")
            .GetProperty("nested");
        Assert.Equal(
            incompatibleNode is "nested-additional-properties",
            nested.GetProperty("additionalProperties").GetBoolean()
        );
    }

    [Fact]
    public void ConvertToApiRequest_RejectsProviderIncompatibleFunctionName() {
        ToolDefinition definition = new(
            "recap_grid.control",
            "Invalid dotted Responses function name.",
            new ToolSchema.Object()
        );

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException
        >(() => Convert(definition));

        Assert.Contains(
            "letters, digits, underscores, or hyphens",
            exception.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ConvertToApiRequest_DisablesStrictForEmptyObjectSchema() {
        ToolDefinition definition = new(
            "no_arguments",
            "No arguments.",
            new ToolSchema.Object()
        );

        OpenAIResponsesTool tool = Assert.Single(Convert(definition).Tools!);

        Assert.False(tool.Strict);
    }

    private static OpenAIResponsesApiRequest Convert(
        ToolDefinition definition
    ) => OpenAIResponsesMessageConverter.ConvertToApiRequest(
        new CompletionRequest(
            "gpt-5",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault([definition]),
                [new ObservationMessage("Use the tool.")]
            ),
            tailMessages: []
        ),
        new OpenAIResponsesClientOptions {
            IncludeEncryptedReasoning = false
        }
    );

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
                                        isNullable: true,
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
                                        isNullable: true,
                                        description: "Filter clauses."
                                    ),
                                    isRequired: true
                                )
                            ],
                            description: "Structured filters.",
                            isNullable: true
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
