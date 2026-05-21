using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Abstractions.Tests;

public sealed class ToolSchemaValueTests {
    [Fact]
    public void Ctor_NonNullableStringWithNullDefault_Throws() {
        var exception = Assert.Throws<ArgumentException>(
            () =>
            new ToolSchema.Value(
                valueKind: ToolParamType.String,
                isNullable: false,
                defaultValue: new ParamDefault(null)
            )
        );

        Assert.Contains("cannot be null", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctor_Int32WithNonIntegerDefault_Throws() {
        var exception = Assert.Throws<ArgumentException>(
            () =>
            new ToolSchema.Value(
                valueKind: ToolParamType.Int32,
                defaultValue: new ParamDefault(3.14)
            )
        );

        Assert.Contains("cannot be assigned", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctor_NullableStringWithNullDefault_PreservesExplicitSchemaMetadata() {
        var definition = new ToolDefinition(
            name: "set_profile",
            description: "Set profile options.",
            inputSchema: new ToolSchema.Object(
                properties: [
                    new ToolSchema.Property(
                        "nickname",
                        new ToolSchema.Value(
                            valueKind: ToolParamType.String,
                            isNullable: true,
                            defaultValue: new ParamDefault(null),
                            description: "Optional nickname."
                        ),
                        isRequired: false
                    )
                ]
            )
        );

        var root = Assert.IsType<ToolSchema.Object>(definition.InputSchema);
        var nickname = Assert.Single(root.Properties);
        Assert.Equal("nickname", nickname.Name);
        Assert.False(nickname.IsRequired);

        var schema = Assert.IsType<ToolSchema.Value>(nickname.Schema);
        Assert.True(schema.IsNullable);
        Assert.True(schema.Default.HasValue);
        var defaultValue = schema.Default.Value.Value;
        Assert.Null(defaultValue);

        var projectedParameter = Assert.Single(definition.Parameters);
        Assert.Equal("nickname", projectedParameter.Name);
        Assert.True(projectedParameter.IsNullable);
        Assert.True(projectedParameter.TryGetDefaultValue(out var projectedDefault));
        Assert.Null(projectedDefault);
    }
}
