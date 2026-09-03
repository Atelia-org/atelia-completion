using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.Gemini.Tests;

public sealed class GeminiToolSchemaProjectionTests {
    [Fact]
    public void ConvertToApiRequest_ProjectsRecursiveToolSchemaIntoGeminiParameters() {
        if (!GeminiProductionTypesPresent()) { return; }

        var request = new CompletionRequest(
            "gemini-2.5-flash",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray.Create(CreateRecursiveToolDefinition())),
                [new ObservationMessage("Search the docs.")]
            ),
            tailMessages: []
        );

        using var document = SerializeApiRequest(ConvertToApiRequest(request));

        var tool = Assert.Single(document.RootElement.GetProperty("tools").EnumerateArray().ToArray());
        var declaration = Assert.Single(tool.GetProperty("functionDeclarations").EnumerateArray().ToArray());

        Assert.Equal("search_docs", declaration.GetProperty("name").GetString());
        AssertJsonSemanticallyEqual(
            """
            {
              "type": "OBJECT",
              "properties": {
                "query": {
                  "type": "STRING",
                  "description": "Query text.",
                  "minLength": 3,
                  "maxLength": 50,
                  "pattern": "^[a-z ]+$"
                },
                "filters": {
                  "type": "OBJECT",
                  "nullable": true,
                  "description": "Structured filters.",
                  "properties": {
                    "mode": {
                      "type": "STRING",
                      "nullable": true,
                      "description": "Search mode.",
                      "enum": ["Exact", "Fuzzy"]
                    },
                    "clauses": {
                      "type": "ARRAY",
                      "nullable": true,
                      "description": "Filter clauses.",
                      "items": {
                        "type": "OBJECT",
                        "description": "Single filter clause.",
                        "properties": {
                          "field": {
                            "type": "STRING",
                            "description": "Target field.",
                            "enum": ["title", "body"]
                          },
                          "boost": {
                            "type": "NUMBER",
                            "description": "Boost weight.",
                            "minimum": 0.1,
                            "maximum": 2
                          },
                          "terms": {
                            "type": "ARRAY",
                            "description": "Terms to match.",
                            "items": {
                              "type": "STRING",
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
            declaration.GetProperty("parameters")
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

    private static object ConvertToApiRequest(CompletionRequest request) {
        var converterType = typeof(CompletionHttpTransportFactory).Assembly.GetType("Atelia.Completion.Gemini.GeminiMessageConverter");
        Assert.NotNull(converterType);
        var method = converterType.GetMethod(
            "ConvertToApiRequest",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(CompletionRequest), typeof(int)],
            modifiers: null
        );

        Assert.NotNull(method);

        try {
            return method!.Invoke(null, [request, 65_536])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static JsonDocument SerializeApiRequest(object apiRequest) {
        var json = JsonSerializer.Serialize(apiRequest, apiRequest.GetType());
        return JsonDocument.Parse(json);
    }

    private static bool GeminiProductionTypesPresent() {
        var assembly = typeof(CompletionHttpTransportFactory).Assembly;
        return assembly.GetType("Atelia.Completion.Gemini.GeminiMessageConverter") is not null;
    }

    private static void AssertJsonSemanticallyEqual(string expectedJson, JsonElement actual) {
        using var expectedDocument = JsonDocument.Parse(expectedJson);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actual),
            $"Expected:\n{expectedDocument.RootElement}\nActual:\n{actual}"
        );
    }
}
