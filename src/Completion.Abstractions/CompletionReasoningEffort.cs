using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// Provider-neutral reasoning / thinking policy preset.
/// Providers map the preset to their own wire-level controls.
/// </summary>
[JsonConverter(typeof(CompletionReasoningEffortJsonConverter))]
public enum CompletionReasoningEffort {
    /// <summary>Do not send an explicit reasoning control; preserve the provider/model default.</summary>
    ProviderDefault,

    /// <summary>
    /// Request non-thinking when supported. A model specification may normalize
    /// this intent to the lowest enabled level for a model that cannot disable
    /// thinking; this is not a guarantee that thinking will be disabled.
    /// </summary>
    Disabled,

    Low,

    Medium,

    High,

    XHigh,

    Max,
}

/// <summary>
/// Stable string-only wire converter for <see cref="CompletionReasoningEffort"/>.
/// Numeric enum values are deliberately rejected so configuration does not silently
/// change meaning when members are reordered or added.
/// </summary>
public sealed class CompletionReasoningEffortJsonConverter
    : JsonConverter<CompletionReasoningEffort> {
    public override CompletionReasoningEffort Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) {
        if (reader.TokenType is not JsonTokenType.String) {
            throw new JsonException("Completion reasoning effort must be a string.");
        }

        return reader.GetString()?.ToLowerInvariant() switch {
            "provider-default" => CompletionReasoningEffort.ProviderDefault,
            "disabled" => CompletionReasoningEffort.Disabled,
            "low" => CompletionReasoningEffort.Low,
            "medium" => CompletionReasoningEffort.Medium,
            "high" => CompletionReasoningEffort.High,
            "xhigh" => CompletionReasoningEffort.XHigh,
            "max" => CompletionReasoningEffort.Max,
            var value => throw new JsonException(
                $"Unsupported completion reasoning effort '{value ?? "<null>"}'."
            )
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        CompletionReasoningEffort value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value switch {
        CompletionReasoningEffort.ProviderDefault => "provider-default",
        CompletionReasoningEffort.Disabled => "disabled",
        CompletionReasoningEffort.Low => "low",
        CompletionReasoningEffort.Medium => "medium",
        CompletionReasoningEffort.High => "high",
        CompletionReasoningEffort.XHigh => "xhigh",
        CompletionReasoningEffort.Max => "max",
        _ => throw new JsonException($"Unsupported completion reasoning effort '{value}'.")
    });
}
