using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

internal static class JsonArgumentParser {
    private static readonly JsonDocumentOptions DocumentOptions = new() {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static ToolArgumentParsingResult ParseArguments(ToolSchema.Object schema, string rawArguments) {
        if (schema is null) { throw new ArgumentNullException(nameof(schema)); }

        var warnings = new List<string>();
        var errors = new List<string>();
        var arguments = ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.Ordinal);
        var rawBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var text = string.IsNullOrWhiteSpace(rawArguments) ? "{}" : rawArguments;

        try {
            using var document = JsonDocument.Parse(text, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                errors.Add(ErrorAt("$", "arguments_must_be_object"));
                rawBuilder.Clear();
            }
            else {
                var parsed = ParseObject(schema, document.RootElement, null, warnings, errors, rawBuilder);
                arguments = parsed;
            }
        }
        catch (JsonException ex) {
            errors.Add($"json_parse_error:{ex.Message}");
        }

        var parseError = errors.Count == 0 ? null : string.Join("; ", errors);
        var parseWarning = warnings.Count == 0 ? null : string.Join("; ", warnings);

        return new ToolArgumentParsingResult(arguments, rawBuilder.ToImmutable(), parseError, parseWarning);
    }

    private static ImmutableDictionary<string, object?> ParseObject(
        ToolSchema.Object schema,
        JsonElement element,
        string? path,
        List<string> warnings,
        List<string> errors,
        ImmutableDictionary<string, string>.Builder rawBuilder
    ) {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        var lookup = CreatePropertyLookup(schema.Properties);
        var seenProperties = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject()) {
            var propertyPath = AppendPropertyPath(path, property.Name);
            rawBuilder[propertyPath] = ExtractRawValue(property.Value);

            if (!lookup.TryGetValue(property.Name, out var schemaProperty)) {
                if (!schema.AdditionalProperties) {
                    errors.Add(ErrorAt(propertyPath, "unknown_property"));
                    continue;
                }

                builder[property.Name] = ConvertUntyped(property.Value);
                continue;
            }

            seenProperties.Add(property.Name);

            var parsed = ParseSchemaValue(schemaProperty.Schema, property.Value, propertyPath, warnings, errors, rawBuilder);
            if (!parsed.IsSuccess) {
                if (!string.IsNullOrEmpty(parsed.Error)) {
                    errors.Add(parsed.Error);
                }

                continue;
            }

            if (!string.IsNullOrEmpty(parsed.Warning)) {
                warnings.Add(parsed.Warning);
            }

            builder[schemaProperty.Name] = parsed.Value;
        }

        foreach (var property in schema.Properties) {
            if (property.IsRequired && !seenProperties.Contains(property.Name)) {
                errors.Add(ErrorAt(AppendPropertyPath(path, property.Name), "missing_required"));
            }
        }

        return builder.ToImmutable();
    }

    private static ParseResult ParseSchemaValue(
        ToolSchema schema,
        JsonElement element,
        string path,
        List<string> warnings,
        List<string> errors,
        ImmutableDictionary<string, string>.Builder rawBuilder
    ) {
        switch (schema) {
            case ToolSchema.Object objectSchema:
                if (element.ValueKind == JsonValueKind.Null) {
                    return ParseResult.CreateError(ErrorAt(path, "null_not_allowed"));
                }

                if (element.ValueKind != JsonValueKind.Object) {
                    return ParseResult.CreateError(ErrorAt(path, "expected_object"));
                }

                var objectErrorCount = errors.Count;
                var parsedObject = ParseObject(objectSchema, element, path, warnings, errors, rawBuilder);
                return errors.Count == objectErrorCount
                    ? ParseResult.CreateSuccess(parsedObject)
                    : ParseResult.CreateFailure();

            case ToolSchema.Array arraySchema:
                return ParseArray(arraySchema, element, path, warnings, errors, rawBuilder);

            case ToolSchema.Value valueSchema:
                return ParseValue(valueSchema, element, path);

            default:
                return ParseResult.CreateError(ErrorAt(path, "unsupported_schema"));
        }
    }

    private static ParseResult ParseArray(
        ToolSchema.Array schema,
        JsonElement element,
        string path,
        List<string> warnings,
        List<string> errors,
        ImmutableDictionary<string, string>.Builder rawBuilder
    ) {
        if (element.ValueKind == JsonValueKind.Null) {
            return schema.IsNullable
                ? ParseResult.CreateSuccess(null)
                : ParseResult.CreateError(ErrorAt(path, "null_not_allowed"));
        }

        if (element.ValueKind != JsonValueKind.Array) {
            return ParseResult.CreateError(ErrorAt(path, "expected_array"));
        }

        var builder = ImmutableArray.CreateBuilder<object?>(element.GetArrayLength());
        var errorCount = errors.Count;

        var index = 0;
        foreach (var item in element.EnumerateArray()) {
            var itemPath = AppendArrayIndex(path, index++);
            rawBuilder[itemPath] = ExtractRawValue(item);

            var parsed = ParseSchemaValue(schema.ItemSchema, item, itemPath, warnings, errors, rawBuilder);
            if (!parsed.IsSuccess) {
                if (!string.IsNullOrEmpty(parsed.Error)) {
                    errors.Add(parsed.Error);
                }

                continue;
            }

            if (!string.IsNullOrEmpty(parsed.Warning)) {
                warnings.Add(parsed.Warning);
            }

            builder.Add(parsed.Value);
        }

        return errors.Count == errorCount
            ? ParseResult.CreateSuccess(builder.ToImmutable())
            : ParseResult.CreateFailure();
    }

    private static ParseResult ParseValue(ToolSchema.Value schema, JsonElement element, string path) {
        if (element.ValueKind == JsonValueKind.Null) {
            return schema.IsNullable
                ? ParseResult.CreateSuccess(null)
                : ParseResult.CreateError(ErrorAt(path, "null_not_allowed"));
        }

        var parsed = schema.ValueKind switch {
            ToolParamType.String => ParseString(element),
            ToolParamType.Boolean => ParseBoolean(element),
            ToolParamType.Int32 => ParseInt32(element),
            ToolParamType.Int64 => ParseInt64(element),
            ToolParamType.Float32 => ParseFloat32(element),
            ToolParamType.Float64 => ParseFloat64(element),
            ToolParamType.Decimal => ParseDecimal(element),
            _ => ParseResult.CreateError("unsupported_value_kind")
        };

        if (!parsed.IsSuccess) {
            return ParseResult.CreateError(ErrorAt(path, parsed.Error ?? "invalid_value"));
        }

        var constraintError = ValidateValueConstraints(schema, parsed.Value);
        if (constraintError is not null) {
            return ParseResult.CreateError(ErrorAt(path, constraintError));
        }

        return ParseResult.CreateSuccess(parsed.Value, WarningAt(path, parsed.Warning));
    }

    private static string? ValidateValueConstraints(ToolSchema.Value schema, object? value) {
        if (value is null) { return null; }

        if (schema.ValueKind == ToolParamType.String) {
            var text = (string)value;

            if (!schema.StringEnumValues.IsDefaultOrEmpty && !schema.StringEnumValues.Contains(text, StringComparer.Ordinal)) {
                return "string_enum_mismatch";
            }

            if (schema.MinLength.HasValue && text.Length < schema.MinLength.Value) {
                return "string_too_short";
            }

            if (schema.MaxLength.HasValue && text.Length > schema.MaxLength.Value) {
                return "string_too_long";
            }

            if (!string.IsNullOrEmpty(schema.Pattern)) {
                try {
                    if (!Regex.IsMatch(text, schema.Pattern, RegexOptions.CultureInvariant)) {
                        return "string_pattern_mismatch";
                    }
                }
                catch (ArgumentException) {
                    return "invalid_string_pattern";
                }
            }

            return null;
        }

        if (schema.Minimum is not null && CompareNumericValues(value, schema.Minimum, schema.ValueKind) < 0) {
            return "number_below_minimum";
        }

        if (schema.Maximum is not null && CompareNumericValues(value, schema.Maximum, schema.ValueKind) > 0) {
            return "number_above_maximum";
        }

        return null;
    }

    private static int CompareNumericValues(object left, object right, ToolParamType valueKind) {
        return valueKind switch {
            ToolParamType.Int32 => Convert.ToInt32(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToInt32(right, CultureInfo.InvariantCulture)),
            ToolParamType.Int64 => Convert.ToInt64(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToInt64(right, CultureInfo.InvariantCulture)),
            ToolParamType.Float32 => Convert.ToSingle(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToSingle(right, CultureInfo.InvariantCulture)),
            ToolParamType.Float64 => Convert.ToDouble(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture)),
            ToolParamType.Decimal => Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(valueKind), valueKind, "Value kind does not support numeric comparison.")
        };
    }

    private static Dictionary<string, ToolSchema.Property> CreatePropertyLookup(ImmutableArray<ToolSchema.Property> properties) {
        var lookup = new Dictionary<string, ToolSchema.Property>(StringComparer.Ordinal);
        foreach (var property in properties) {
            lookup[property.Name] = property;
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

    private static ParseResult ParseString(JsonElement element) {
        return element.ValueKind == JsonValueKind.String
            ? ParseResult.CreateSuccess(element.GetString())
            : ParseResult.CreateError("expected_string");
    }

    private static ParseResult ParseBoolean(JsonElement element) {
        return element.ValueKind switch {
            JsonValueKind.True => ParseResult.CreateSuccess(true),
            JsonValueKind.False => ParseResult.CreateSuccess(false),
            _ => ParseResult.CreateError("expected_boolean")
        };
    }

    private static ParseResult ParseInt32(JsonElement element) {
        if (element.ValueKind != JsonValueKind.Number) { return ParseResult.CreateError("expected_integer"); }
        if (element.TryGetInt32(out var value32)) { return ParseResult.CreateSuccess(value32); }
        if (!IsIntegerLiteral(element)) { return ParseResult.CreateError("expected_integer"); }
        return ParseResult.CreateError("int32_out_of_range");
    }

    private static ParseResult ParseInt64(JsonElement element) {
        if (element.ValueKind != JsonValueKind.Number) { return ParseResult.CreateError("expected_integer"); }
        if (element.TryGetInt64(out var integer)) { return ParseResult.CreateSuccess(integer); }
        if (!IsIntegerLiteral(element)) { return ParseResult.CreateError("expected_integer"); }
        return ParseResult.CreateError("int64_out_of_range");
    }

    private static ParseResult ParseFloat32(JsonElement element) {
        double number;
        var sourceKind = element.ValueKind;

        if (sourceKind == JsonValueKind.Number) {
            if (!element.TryGetDouble(out number)) { return ParseResult.CreateError("float32_invalid_literal"); }
        }
        else { return ParseResult.CreateError("expected_number"); }

        if (double.IsNaN(number) || double.IsInfinity(number)) { return ParseResult.CreateError("float32_invalid_literal"); }
        if (number is < -float.MaxValue or > float.MaxValue) { return ParseResult.CreateError("float32_out_of_range"); }

        var converted = (float)number;
        var delta = Math.Abs(number - converted);

        string? warning = delta > double.Epsilon ? "float64_precision_loss" : null;

        return ParseResult.CreateSuccess(converted, warning);
    }

    private static ParseResult ParseFloat64(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Number) {
            return element.TryGetDouble(out var value)
                ? ParseResult.CreateSuccess(value)
                : ParseResult.CreateError("float64_invalid_literal");
        }

        return ParseResult.CreateError("expected_number");
    }

    private static ParseResult ParseDecimal(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Number) {
            return element.TryGetDecimal(out var value)
                ? ParseResult.CreateSuccess(value)
                : ParseResult.CreateError("decimal_invalid_literal");
        }

        return ParseResult.CreateError("expected_number");
    }

    private static ImmutableDictionary<string, object?> ConvertObject(JsonElement element) {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
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

    private static string AppendPropertyPath(string? prefix, string propertyName)
        => string.IsNullOrEmpty(prefix) ? propertyName : string.Concat(prefix, ".", propertyName);

    private static string AppendArrayIndex(string prefix, int index)
        => string.Concat(prefix, "[", index.ToString(CultureInfo.InvariantCulture), "]");

    private static string ErrorAt(string path, string error)
        => string.Concat(path, ":", error);

    private static string? WarningAt(string path, string? warning)
        => string.IsNullOrEmpty(warning) ? null : string.Concat(path, ":", warning);

    private static bool IsIntegerLiteral(JsonElement element) {
        if (element.ValueKind != JsonValueKind.Number) { return false; }

        var rawText = element.GetRawText();
        foreach (var ch in rawText) {
            if (ch is '.' or 'e' or 'E') { return false; }
        }

        return true;
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

        public static ParseResult CreateError(string error)
            => new(false, null, null, error);

        public static ParseResult CreateFailure()
            => new(false, null, null, null);
    }
}
