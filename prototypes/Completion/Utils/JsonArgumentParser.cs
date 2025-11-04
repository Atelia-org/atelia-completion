using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Utils;

/// <summary>
/// Provides JSON based argument parsing for providers that receive tool calls as structured payloads.
/// </summary>
/// <remarks>
/// Parsing always attempts to recover original parameter text so that callers can rehydrate failed invocations.
/// </remarks>
internal static class JsonArgumentParser {
    private static readonly JsonDocumentOptions DocumentOptions = new() {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    // public static ToolCallRequest CreateRequest(ITool tool, string toolCallId, string rawArguments) {
    //     if (tool is null) { throw new ArgumentNullException(nameof(tool)); }
    //     if (toolCallId is null) { throw new ArgumentNullException(nameof(toolCallId)); }

    //     var result = ParseArguments(tool, rawArguments);
    //     return new ToolCallRequest(tool.Name, toolCallId, rawArguments ?? string.Empty, result.Arguments, result.ParseError, result.ParseWarning);
    // }

    /// <summary>
    /// Parses raw JSON arguments against the parameter specification.
    /// </summary>
    /// <remarks>
    /// Even when an individual parameter fails to parse, the raw textual representation is preserved in the returned
    /// <see cref="ToolArgumentParsingResult.RawArguments"/> map.
    /// </remarks>
    public static ToolArgumentParsingResult ParseArguments(IReadOnlyList<ToolParamSpec> parameters, string rawArguments) {
        if (parameters is null) { throw new ArgumentNullException(nameof(parameters)); }

        var warnings = new List<string>();
        var errors = new List<string>();
        var arguments = ImmutableDictionary<string, object?>.Empty;
        var rawBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments;

        try {
            using var document = JsonDocument.Parse(text, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                errors.Add("arguments_must_be_object");
                rawBuilder.Clear();
            }
            else {
                arguments = ParseObject(parameters, document.RootElement, warnings, errors, rawBuilder);
            }
        }
        catch (JsonException ex) {
            errors.Add($"json_parse_error:{ex.Message}");
        }

        foreach (var parameter in parameters) {
            if (parameter.IsRequired && !arguments.ContainsKey(parameter.Name)) {
                errors.Add($"missing_required:{parameter.Name}");
            }
        }

        var parseError = errors.Count == 0 ? null : string.Join("; ", errors);
        var parseWarning = warnings.Count == 0 ? null : string.Join("; ", warnings);

        return new ToolArgumentParsingResult(arguments, rawBuilder.ToImmutable(), parseError, parseWarning);
    }

    private static ImmutableDictionary<string, object?> ParseObject(
        IReadOnlyList<ToolParamSpec> parameters,
        JsonElement element,
        List<string> warnings,
        List<string> errors,
        ImmutableDictionary<string, string>.Builder rawBuilder
    ) {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        var lookup = CreateParameterLookup(parameters);

        foreach (var property in element.EnumerateObject()) {
            rawBuilder[property.Name] = ExtractRawValue(property.Value);

            if (!lookup.TryGetValue(property.Name, out var parameter)) {
                builder[property.Name] = ConvertUntyped(property.Value);
                warnings.Add($"unknown_parameter:{property.Name}");
                continue;
            }

            var parsed = ParseValue(parameter, property.Value);
            if (!parsed.IsSuccess) {
                errors.Add($"{parameter.Name}:{parsed.Error}");
                continue;
            }

            if (!string.IsNullOrEmpty(parsed.Warning)) {
                warnings.Add($"{parameter.Name}:{parsed.Warning}");
            }

            builder[parameter.Name] = parsed.Value;
        }

        return builder.ToImmutable();
    }

    private static Dictionary<string, ToolParamSpec> CreateParameterLookup(IReadOnlyList<ToolParamSpec> parameters) {
        var lookup = new Dictionary<string, ToolParamSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters) {
            lookup[parameter.Name] = parameter;
        }

        return lookup;
    }

    private static string ExtractRawValue(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Object => element.GetRawText(),
            JsonValueKind.Array => element.GetRawText(),
            _ => element.GetRawText()
        };
    }

    private static ParseResult ParseValue(ToolParamSpec parameter, JsonElement element) {
        if (element.ValueKind == JsonValueKind.Null) {
            return parameter.IsNullable
                ? ParseResult.CreateSuccess(null)
                : ParseResult.CreateError("null_not_allowed");
        }

        var result = parameter.ValueKind switch {
            ToolParamType.String => ParseString(element),
            ToolParamType.Boolean => ParseBoolean(element),
            ToolParamType.Int32 => ParseInt32(element),
            ToolParamType.Int64 => ParseInt64(element),
            ToolParamType.Float32 => ParseFloat32(element),
            ToolParamType.Float64 => ParseFloat64(element),
            ToolParamType.Decimal => ParseDecimal(element),
            _ => ParseResult.CreateError("unsupported_value_kind")
        };

        if (!result.IsSuccess) { return result; }

        // TODO: 引入统一的参数约束校验流程（枚举、范围等），在此处接入。
        return result;
    }

    private static ParseResult ParseString(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.String => ParseResult.CreateSuccess(element.GetString()),
            JsonValueKind.True or JsonValueKind.False => ParseResult.CreateSuccess(element.GetBoolean().ToString()),
            JsonValueKind.Number => ParseResult.CreateSuccess(element.GetRawText(), "non_string_literal_retained"),
            _ => ParseResult.CreateSuccess(element.GetRawText(), "non_string_literal_retained")
        };
    }

    private static ParseResult ParseBoolean(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.True => ParseResult.CreateSuccess(true),
            JsonValueKind.False => ParseResult.CreateSuccess(false),
            JsonValueKind.String => ParseBooleanString(element.GetString()),
            JsonValueKind.Number => ParseBooleanNumber(element),
            _ => ParseResult.CreateError("unsupported_boolean_literal")
        };
    }

    private static ParseResult ParseBooleanString(string? text) {
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) { return ParseResult.CreateSuccess(true, "string_literal_converted_to_boolean"); }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) { return ParseResult.CreateSuccess(false, "string_literal_converted_to_boolean"); }

        return ParseResult.CreateError("invalid_boolean_string");
    }

    private static ParseResult ParseBooleanNumber(JsonElement element) {
        if (!element.TryGetDouble(out var value)) { return ParseResult.CreateError("invalid_boolean_number"); }
        return ParseResult.CreateSuccess(Math.Abs(value) > double.Epsilon, "number_coerced_to_boolean");
    }

    private static ParseResult ParseInt32(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Number) {
            if (element.TryGetInt32(out var value32)) { return ParseResult.CreateSuccess(value32); }

            if (element.TryGetInt64(out var value64)) {
                return value64 is >= int.MinValue and <= int.MaxValue
                    ? ParseResult.CreateSuccess((int)value64, "int64_coerced_to_int32")
                    : ParseResult.CreateError("int32_out_of_range");
            }

            if (element.TryGetDouble(out var number)) {
                if (double.IsNaN(number) || double.IsInfinity(number)) { return ParseResult.CreateError("int32_invalid_number"); }

                var truncated = Math.Truncate(number);
                if (truncated is < int.MinValue or > int.MaxValue) { return ParseResult.CreateError("int32_out_of_range"); }

                var warning = Math.Abs(number - truncated) > double.Epsilon
                    ? "fractional_number_truncated_to_int32"
                    : "float64_coerced_to_int32";
                return ParseResult.CreateSuccess((int)truncated, warning);
            }

            return ParseResult.CreateError("int32_invalid_literal");
        }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) { return ParseResult.CreateSuccess(value, "string_literal_converted_to_int32"); }
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)) {
                return longValue is >= int.MinValue and <= int.MaxValue
                    ? ParseResult.CreateSuccess((int)longValue, "string_literal_converted_to_int32")
                    : ParseResult.CreateError("int32_out_of_range");
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
                var truncated = Math.Truncate(number);
                if (truncated is < int.MinValue or > int.MaxValue) { return ParseResult.CreateError("int32_out_of_range"); }

                var warning = Math.Abs(number - truncated) > double.Epsilon
                    ? "fractional_string_truncated_to_int32"
                    : "string_literal_converted_to_int32";
                return ParseResult.CreateSuccess((int)truncated, warning);
            }

            return ParseResult.CreateError("int32_invalid_string");
        }

        return ParseResult.CreateError("int32_unsupported_literal");
    }

    private static ParseResult ParseInt64(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Number) {
            if (element.TryGetInt64(out var integer)) { return ParseResult.CreateSuccess(integer); }

            if (element.TryGetDouble(out var number)) {
                if (double.IsNaN(number) || double.IsInfinity(number)) { return ParseResult.CreateError("int64_invalid_number"); }

                var truncated = Math.Truncate(number);
                if (truncated is < long.MinValue or > long.MaxValue) { return ParseResult.CreateError("int64_out_of_range"); }

                var warning = Math.Abs(number - truncated) > double.Epsilon
                    ? "fractional_number_truncated_to_int64"
                    : "float64_coerced_to_int64";
                return ParseResult.CreateSuccess((long)truncated, warning);
            }

            return ParseResult.CreateError("int64_invalid_literal");
        }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) { return ParseResult.CreateSuccess(integer, "string_literal_converted_to_int64"); }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
                var truncated = Math.Truncate(number);
                if (truncated is < long.MinValue or > long.MaxValue) { return ParseResult.CreateError("int64_out_of_range"); }

                var warning = Math.Abs(number - truncated) > double.Epsilon
                    ? "fractional_string_truncated_to_int64"
                    : "string_literal_converted_to_int64";
                return ParseResult.CreateSuccess((long)truncated, warning);
            }

            return ParseResult.CreateError("int64_invalid_string");
        }

        return ParseResult.CreateError("int64_unsupported_literal");
    }

    private static ParseResult ParseFloat32(JsonElement element) {
        double number;
        var sourceKind = element.ValueKind;

        if (sourceKind == JsonValueKind.Number) {
            if (!element.TryGetDouble(out number)) { return ParseResult.CreateError("float32_invalid_literal"); }
        }
        else if (sourceKind == JsonValueKind.String) {
            var text = element.GetString();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) { return ParseResult.CreateError("float32_invalid_string"); }
        }
        else { return ParseResult.CreateError("float32_unsupported_literal"); }

        if (double.IsNaN(number) || double.IsInfinity(number)) { return ParseResult.CreateError("float32_invalid_literal"); }
        if (number is < -float.MaxValue or > float.MaxValue) { return ParseResult.CreateError("float32_out_of_range"); }

        var converted = (float)number;
        var delta = Math.Abs(number - converted);

        string? warning = null;
        if (sourceKind == JsonValueKind.String) {
            warning = delta > double.Epsilon ? "string_literal_precision_loss" : "string_literal_converted_to_float32";
        }
        else if (delta > double.Epsilon) {
            warning = "float64_precision_loss";
        }

        return ParseResult.CreateSuccess(converted, warning);
    }

    private static ParseResult ParseFloat64(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Number) {
            return element.TryGetDouble(out var value)
                ? ParseResult.CreateSuccess(value)
                : ParseResult.CreateError("float64_invalid_literal");
        }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? ParseResult.CreateSuccess(value, "string_literal_converted_to_float64")
                : ParseResult.CreateError("float64_invalid_string");
        }

        return ParseResult.CreateError("float64_unsupported_literal");
    }

    private static ParseResult ParseDecimal(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Number) {
            return element.TryGetDecimal(out var value)
                ? ParseResult.CreateSuccess(value)
                : ParseResult.CreateError("decimal_invalid_literal");
        }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? ParseResult.CreateSuccess(value, "string_literal_converted_to_decimal")
                : ParseResult.CreateError("decimal_invalid_string");
        }

        return ParseResult.CreateError("decimal_unsupported_literal");
    }

    private static ImmutableDictionary<string, object?> ConvertObject(JsonElement element) {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject()) {
            builder[property.Name] = ConvertUntyped(property.Value);
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<object?> ConvertArray(JsonElement element) {
        var builder = ImmutableArray.CreateBuilder<object?>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray()) {
            builder.Add(ConvertUntyped(item));
        }
        return builder.ToImmutable();
    }

    private static object? ConvertUntyped(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            JsonValueKind.Object => ConvertObject(element),
            JsonValueKind.Array => ConvertArray(element),
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private readonly struct ParseResult {
        private ParseResult(bool isSuccess, object? value, string? warning, string? error) {
            IsSuccess = isSuccess;
            Value = value;
            Warning = warning;
            Error = error;
        }

        public bool IsSuccess { get; }
        public object? Value { get; }
        public string? Warning { get; }
        public string? Error { get; }

        public static ParseResult CreateSuccess(object? value, string? warning = null)
            => new(true, value, warning, null);

        public static ParseResult CreateWarning(object? value, string warning)
            => new(true, value, warning, null);

        public static ParseResult CreateError(string error)
            => new(false, null, null, error);
    }
}
