using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Atelia.Completion.Abstractions;

internal static class CompletionOutputContractCanonicalizer {
    private const string Schema =
        "atelia.completion.output-contract.v1";

    private static readonly JsonWriterOptions WriterOptions = new() {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false
    };

    internal static string ComputeFingerprint(
        CompletionOutputContract contract
    ) {
        ArgumentNullException.ThrowIfNull(contract);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteStartArray("tools");
            foreach (ToolDefinition definition in contract.Tools) {
                WriteToolDefinition(writer, definition);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("toolChoice");
            writer.WriteString(
                "kind",
                WriteToolChoiceKind(contract.ToolChoice.Kind)
            );
            WriteNullableString(
                writer,
                "requiredToolName",
                contract.ToolChoice.RequiredToolName
            );
            writer.WriteEndObject();
            if (contract.AllowParallelToolCalls is bool allowParallel) {
                writer.WriteBoolean(
                    "allowParallelToolCalls",
                    allowParallel
                );
            }
            else {
                writer.WriteNull("allowParallelToolCalls");
            }
            writer.WriteEndObject();
        }

        return "sha256:"
            + Convert.ToHexStringLower(
                SHA256.HashData(buffer.WrittenSpan)
            );
    }

    private static void WriteToolDefinition(
        Utf8JsonWriter writer,
        ToolDefinition definition
    ) {
        writer.WriteStartObject();
        writer.WriteString("name", definition.Name);
        writer.WriteString("description", definition.Description);
        writer.WritePropertyName("inputSchema");
        WriteToolSchema(writer, definition.InputSchema);
        writer.WriteEndObject();
    }

    private static void WriteToolSchema(
        Utf8JsonWriter writer,
        ToolSchema schema
    ) {
        writer.WriteStartObject();
        switch (schema) {
            case ToolSchema.Object objectSchema:
                writer.WriteString("kind", "object");
                WriteNullableString(
                    writer,
                    "description",
                    objectSchema.Description
                );
                WriteNullableString(
                    writer,
                    "example",
                    objectSchema.Example
                );
                writer.WriteBoolean(
                    "additionalProperties",
                    objectSchema.AdditionalProperties
                );
                writer.WriteStartArray("properties");
                foreach (ToolSchema.Property property
                    in objectSchema.Properties) {
                    writer.WriteStartObject();
                    writer.WriteString("name", property.Name);
                    writer.WriteBoolean(
                        "required",
                        property.IsRequired
                    );
                    writer.WritePropertyName("schema");
                    WriteToolSchema(writer, property.Schema);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                break;
            case ToolSchema.Array arraySchema:
                writer.WriteString("kind", "array");
                WriteNullableString(
                    writer,
                    "description",
                    arraySchema.Description
                );
                WriteNullableString(
                    writer,
                    "example",
                    arraySchema.Example
                );
                writer.WriteBoolean("nullable", arraySchema.IsNullable);
                writer.WritePropertyName("items");
                WriteToolSchema(writer, arraySchema.ItemSchema);
                break;
            case ToolSchema.Value valueSchema:
                writer.WriteString("kind", "value");
                WriteNullableString(
                    writer,
                    "description",
                    valueSchema.Description
                );
                WriteNullableString(
                    writer,
                    "example",
                    valueSchema.Example
                );
                writer.WriteString(
                    "valueKind",
                    WriteToolParamType(valueSchema.ValueKind)
                );
                writer.WriteBoolean("nullable", valueSchema.IsNullable);
                writer.WriteBoolean(
                    "hasDefault",
                    valueSchema.Default.HasValue
                );
                if (valueSchema.Default.HasValue) {
                    writer.WritePropertyName("default");
                    WriteTypedValue(
                        writer,
                        valueSchema.ValueKind,
                        valueSchema.Default.Value.Value
                    );
                }
                writer.WriteStartArray("stringEnumValues");
                foreach (string item
                    in valueSchema.StringEnumValues) {
                    writer.WriteStringValue(item);
                }
                writer.WriteEndArray();
                WriteNullableNumber(
                    writer,
                    "minLength",
                    valueSchema.MinLength
                );
                WriteNullableNumber(
                    writer,
                    "maxLength",
                    valueSchema.MaxLength
                );
                WriteNullableString(
                    writer,
                    "pattern",
                    valueSchema.Pattern
                );
                writer.WriteBoolean(
                    "hasMinimum",
                    valueSchema.Minimum is not null
                );
                if (valueSchema.Minimum is not null) {
                    writer.WritePropertyName("minimum");
                    WriteTypedValue(
                        writer,
                        valueSchema.ValueKind,
                        valueSchema.Minimum
                    );
                }
                writer.WriteBoolean(
                    "hasMaximum",
                    valueSchema.Maximum is not null
                );
                if (valueSchema.Maximum is not null) {
                    writer.WritePropertyName("maximum");
                    WriteTypedValue(
                        writer,
                        valueSchema.ValueKind,
                        valueSchema.Maximum
                    );
                }
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported tool schema type '{schema.GetType().FullName}'."
                );
        }
        writer.WriteEndObject();
    }

    private static void WriteTypedValue(
        Utf8JsonWriter writer,
        ToolParamType kind,
        object? value
    ) {
        if (value is null) {
            writer.WriteNullValue();
            return;
        }

        switch (kind) {
            case ToolParamType.String:
                writer.WriteStringValue((string)value);
                break;
            case ToolParamType.Boolean:
                writer.WriteBooleanValue((bool)value);
                break;
            case ToolParamType.Int32:
                writer.WriteNumberValue(Convert.ToInt32(
                    value,
                    CultureInfo.InvariantCulture
                ));
                break;
            case ToolParamType.Int64:
                writer.WriteNumberValue(Convert.ToInt64(
                    value,
                    CultureInfo.InvariantCulture
                ));
                break;
            case ToolParamType.Float32:
                WriteSingle(writer, (float)value);
                break;
            case ToolParamType.Float64:
                WriteDouble(writer, (double)value);
                break;
            case ToolParamType.Decimal:
                writer.WriteNumberValue((decimal)value);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported tool parameter type '{kind}'."
                );
        }
    }

    private static void WriteSingle(
        Utf8JsonWriter writer,
        float value
    ) {
        if (float.IsFinite(value)) {
            writer.WriteNumberValue(value);
            return;
        }
        writer.WriteStringValue(float.IsNaN(value)
            ? "NaN"
            : float.IsPositiveInfinity(value)
                ? "+Infinity"
                : "-Infinity");
    }

    private static void WriteDouble(
        Utf8JsonWriter writer,
        double value
    ) {
        if (double.IsFinite(value)) {
            writer.WriteNumberValue(value);
            return;
        }
        writer.WriteStringValue(double.IsNaN(value)
            ? "NaN"
            : double.IsPositiveInfinity(value)
                ? "+Infinity"
                : "-Infinity");
    }

    private static string WriteToolChoiceKind(
        CompletionToolChoiceKind kind
    ) => kind switch {
        CompletionToolChoiceKind.ProviderDefault => "provider-default",
        CompletionToolChoiceKind.Auto => "auto",
        CompletionToolChoiceKind.None => "none",
        CompletionToolChoiceKind.RequiredAny => "required-any",
        CompletionToolChoiceKind.RequiredNamed => "required-named",
        _ => throw new InvalidOperationException(
            $"Unsupported tool choice kind '{kind}'."
        )
    };

    private static string WriteToolParamType(ToolParamType kind)
        => kind switch {
            ToolParamType.String => "string",
            ToolParamType.Boolean => "boolean",
            ToolParamType.Int32 => "int32",
            ToolParamType.Int64 => "int64",
            ToolParamType.Float32 => "float32",
            ToolParamType.Float64 => "float64",
            ToolParamType.Decimal => "decimal",
            _ => throw new InvalidOperationException(
                $"Unsupported tool parameter type '{kind}'."
            )
        };

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value
    ) {
        if (value is null) {
            writer.WriteNull(propertyName);
        }
        else {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        int? value
    ) {
        if (value is int number) {
            writer.WriteNumber(propertyName, number);
        }
        else {
            writer.WriteNull(propertyName);
        }
    }
}
