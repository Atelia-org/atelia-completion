using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Utils.Tests;

public sealed class ToolArgumentParserTests {
    [Fact]
    public void ParseArguments_Float32PrecisionLossProducesWarning() {
        var parameters = new[] {
            new ToolParamSpec(name: "value", description: "float32-value", valueKind: ToolParamType.Float32)
        };

        var payload = "{\"value\": 123456.7890123}";
        var result = JsonArgumentParser.ParseArguments(parameters, payload);

        Assert.Null(result.ParseError);
        Assert.NotNull(result.ParseWarning);
        Assert.Contains("float64_precision_loss", result.ParseWarning!, StringComparison.Ordinal);

        Assert.Equal("123456.7890123", result.RawArguments["value"]);

        Assert.True(result.Arguments.TryGetValue("value", out var value));
        var floatValue = Assert.IsType<float>(value);
        Assert.Equal((float)123456.7890123, floatValue);
    }

    [Fact]
    public void ParseArguments_DecimalFromStringProducesWarning() {
        var parameters = new[] {
            new ToolParamSpec(name: "amount", description: "amount", valueKind: ToolParamType.Decimal)
        };

        var payload = "{\"amount\": \"42.42\"}";
        var result = JsonArgumentParser.ParseArguments(parameters, payload);

        Assert.Null(result.ParseError);
        Assert.NotNull(result.ParseWarning);
        Assert.Contains("string_literal_converted_to_decimal", result.ParseWarning!, StringComparison.Ordinal);

        Assert.Equal("42.42", result.RawArguments["amount"]);

        var decimalValue = Assert.IsType<decimal>(result.Arguments["amount"]);
        Assert.Equal(42.42m, decimalValue);
    }

    [Fact]
    public void ParseArguments_Int32OutOfRangeReturnsError() {
        var parameters = new[] {
            new ToolParamSpec(name: "count", description: "count", valueKind: ToolParamType.Int32)
        };

        var payload = "{\"count\": 5000000000}";
        var result = JsonArgumentParser.ParseArguments(parameters, payload);

        Assert.Null(result.ParseWarning);
        Assert.NotNull(result.ParseError);
        Assert.Contains("int32_out_of_range", result.ParseError!, StringComparison.Ordinal);

        Assert.Equal("5000000000", result.RawArguments["count"]);
        Assert.False(result.Arguments.ContainsKey("count"));
    }

    [Fact]
    public void ParseArguments_OptionalParameterMissing_NoError() {
        var parameters = new[] {
            new ToolParamSpec(name: "note", description: "note", valueKind: ToolParamType.String,
                isNullable: true,
                defaultValue: new ParamDefault(null)
            )
        };

        var result = JsonArgumentParser.ParseArguments(parameters, "{}");

        Assert.Null(result.ParseError);
        Assert.Null(result.ParseWarning);
        Assert.Empty(result.Arguments);
        Assert.Empty(result.RawArguments);
    }

    [Fact]
    public void ParseArguments_RequiredStringIsNullable_SucceedsWithNullValue() {
        var parameters = new[] {
            new ToolParamSpec(name: "comment", description: "comment", valueKind: ToolParamType.String,
                isNullable: true
            )
        };

        var result = JsonArgumentParser.ParseArguments(parameters, "{\"comment\": null}");

        Assert.Null(result.ParseError);
        Assert.Null(result.ParseWarning);

        Assert.Equal("null", result.RawArguments["comment"]);
        Assert.True(result.Arguments.TryGetValue("comment", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void ParseArguments_NullForNonNullableOptionalParameter_ProducesError() {
        var parameters = new[] {
            new ToolParamSpec(name: "count", description: "count", valueKind: ToolParamType.Int32,
                defaultValue: new ParamDefault(0)
            )
        };

        var result = JsonArgumentParser.ParseArguments(parameters, "{\"count\": null}");

        Assert.Null(result.ParseWarning);
        Assert.NotNull(result.ParseError);
        Assert.Contains("count:null_not_allowed", result.ParseError!, StringComparison.Ordinal);
        Assert.Equal("null", result.RawArguments["count"]);
    }
}
