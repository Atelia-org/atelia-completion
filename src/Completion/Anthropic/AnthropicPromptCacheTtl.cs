using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.Completion.Anthropic;

/// <summary>
/// Anthropic prompt-cache TTL policy. This is intentionally provider-specific:
/// other providers expose different cache lifetime semantics and wire controls.
/// </summary>
[JsonConverter(typeof(AnthropicPromptCacheTtlJsonConverter))]
public enum AnthropicPromptCacheTtl {
    /// <summary>Omit <c>ttl</c> and preserve the Anthropic API default.</summary>
    ProviderDefault,

    FiveMinutes,

    OneHour,
}

/// <summary>
/// Stable string-only configuration converter for
/// <see cref="AnthropicPromptCacheTtl"/>.
/// </summary>
public sealed class AnthropicPromptCacheTtlJsonConverter
    : JsonConverter<AnthropicPromptCacheTtl> {
    public override AnthropicPromptCacheTtl Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) {
        if (reader.TokenType is not JsonTokenType.String) {
            throw new JsonException("Anthropic prompt cache TTL must be a string.");
        }

        return reader.GetString()?.ToLowerInvariant() switch {
            "provider-default" => AnthropicPromptCacheTtl.ProviderDefault,
            "5m" => AnthropicPromptCacheTtl.FiveMinutes,
            "1h" => AnthropicPromptCacheTtl.OneHour,
            var value => throw new JsonException(
                $"Unsupported Anthropic prompt cache TTL '{value ?? "<null>"}'."
            )
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        AnthropicPromptCacheTtl value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(value switch {
        AnthropicPromptCacheTtl.ProviderDefault => "provider-default",
        AnthropicPromptCacheTtl.FiveMinutes => "5m",
        AnthropicPromptCacheTtl.OneHour => "1h",
        _ => throw new JsonException(
            $"Unsupported Anthropic prompt cache TTL '{value}'."
        )
    });
}
