using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Utils;
using Xunit;

namespace Atelia.Completion.Declaration.Tests;

public sealed class ReflectedToolDefinitionBuilderTests {
    [Fact]
    public void Build_FromAttributedRecordClass_GeneratesNestedToolDefinitionSchema_AndKeepsC2CompatibilityProjectionEmpty() {
        var definition = ReflectedToolDefinitionBuilder.Build<SearchDocsRequest>("search_docs");

        Assert.Equal("search_docs", definition.Name);
        Assert.Equal("Search documentation with structured filters.", definition.Description);
        Assert.IsType<ToolSchema.Object>(definition.InputSchema);
        // C2 compatibility must not invent a flat projection for reflected nested schemas.
        Assert.Empty(definition.Parameters);

        var schema = JsonToolSchemaBuilder.BuildSchema(definition);

        AssertJsonSemanticallyEqual(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "q": {
                  "type": "string",
                  "description": "Query text.",
                  "minLength": 3,
                  "maxLength": 50,
                  "pattern": "^[a-z ]+$"
                },
                "Mode": {
                  "type": "string",
                  "description": "Search mode.",
                  "enum": ["Exact", "Fuzzy"]
                },
                "Filters": {
                  "type": "object",
                  "additionalProperties": false,
                  "description": "Structured filters.",
                  "properties": {
                    "Cursor": {
                      "type": "integer",
                      "format": "int64",
                      "description": "Cursor offset.",
                      "minimum": 0,
                      "maximum": 99
                    },
                    "Threshold": {
                      "type": "number",
                      "format": "float64",
                      "description": "Score threshold.",
                      "minimum": 0.1,
                      "maximum": 1
                    },
                    "Label": {
                      "type": "string",
                      "description": "Label"
                    }
                  },
                  "required": ["Cursor", "Threshold"]
                },
                "Tags": {
                  "type": "array",
                  "description": "Requested tags.",
                  "items": {
                    "type": "string"
                  }
                },
                "Limit": {
                  "type": "integer",
                  "format": "int32",
                  "description": "Maximum result count.",
                  "minimum": 1,
                  "maximum": 10
                }
              },
              "required": ["q", "Mode", "Filters", "Tags", "Limit"]
            }
            """,
            schema
        );
    }

    [Fact]
    public void Build_CaseInsensitiveJsonNameCollision_Throws() {
        var ex = Assert.Throws<InvalidOperationException>(() => ReflectedToolDefinitionBuilder.Build<DuplicateNameRequest>("dup_name"));
        Assert.Contains("differ only by case", ex.Message);
    }

    [Fact]
    public void Build_CycleReference_Throws() {
        var ex = Assert.Throws<NotSupportedException>(() => ReflectedToolDefinitionBuilder.Build<CyclicRequest>("cycle"));
        Assert.Contains("Cycle detected", ex.Message);
    }

    [Fact]
    public void Build_FlagsEnum_Throws() {
        var ex = Assert.Throws<NotSupportedException>(() => ReflectedToolDefinitionBuilder.Build<FlagsEnumRequest>("flags_enum"));
        Assert.Contains("Flags enum", ex.Message);
    }

    [Fact]
    public void Build_UnsupportedJsonIgnoreCondition_Throws() {
        var ex = Assert.Throws<NotSupportedException>(() => ReflectedToolDefinitionBuilder.Build<ConditionalIgnoreRequest>("conditional_ignore"));
        Assert.Contains("JsonIgnoreCondition", ex.Message);
    }

    [Fact]
    public void Build_MissingRootDescription_Throws() {
        var ex = Assert.Throws<InvalidOperationException>(() => ReflectedToolDefinitionBuilder.Build<MissingRootDescriptionRequest>("missing_description"));
        Assert.Contains("missing [Description]", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertJsonSemanticallyEqual(string expectedJson, JsonElement actual) {
        using var expectedDocument = JsonDocument.Parse(expectedJson);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actual),
            $"Expected:\n{expectedDocument.RootElement}\nActual:\n{actual}"
        );
    }

    [Description("Search documentation with structured filters.")]
    private sealed record class SearchDocsRequest(
        [property: Description("Query text.")]
        [property: StringLength(50, MinimumLength = 3)]
        [property: RegularExpression("^[a-z ]+$")]
        [property: JsonPropertyName("q")]
        string Query,
        [property: Description("Search mode.")]
        SearchMode Mode,
        [property: Description("Structured filters.")]
        SearchFilters Filters,
        [property: Description("Requested tags.")]
        IReadOnlyList<string> Tags,
        [property: Description("Maximum result count.")]
        [property: Required]
        [property: Range(1, 10)]
        int? Limit,
        [property: JsonIgnore]
        [property: Description("Hidden from schema.")]
        string Hidden = "secret"
    );

    private enum SearchMode {
        Exact,
        Fuzzy
    }

    private sealed class SearchFilters {
        [Description("Cursor offset.")]
        [Range(typeof(long), "0", "99")]
        public long Cursor { get; init; }

        [Description("Score threshold.")]
        [Range(0.1, 1.0)]
        public double Threshold { get; init; }

        public string? Label { get; init; }
    }

    [Description("Duplicate case-insensitive names.")]
    private sealed class DuplicateNameRequest {
        [Description("First value.")]
        [JsonPropertyName("Query")]
        public string Query { get; init; } = string.Empty;

        [Description("Second value.")]
        [JsonPropertyName("query")]
        public string QueryLower { get; init; } = string.Empty;
    }

    [Description("Cycle root.")]
    private sealed class CyclicRequest {
        [Description("Entry node.")]
        public CyclicNode Node { get; init; } = new();
    }

    private sealed class CyclicNode {
        [Description("Next node.")]
        public CyclicNode Next { get; init; } = new();
    }

    [Description("Flags enum request.")]
    private sealed class FlagsEnumRequest {
        [Description("Flag values.")]
        public BadFlags Flags { get; init; }
    }

    [Flags]
    private enum BadFlags {
        One = 1,
        Two = 2
    }

    [Description("Conditional ignore request.")]
    private sealed class ConditionalIgnoreRequest {
        [Description("Ignored conditionally.")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OptionalValue { get; init; }
    }

    private sealed class MissingRootDescriptionRequest {
        [Description("Value.")]
        public string Value { get; init; } = string.Empty;
    }
}
