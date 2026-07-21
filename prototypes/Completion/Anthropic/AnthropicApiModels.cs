using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.Completion.Anthropic;

/// <summary>
/// Anthropic Messages API 请求 DTO。
/// </summary>
internal sealed class AnthropicApiRequest {
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<AnthropicMessage> Messages { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 200_000;

    /// <summary>
    /// system 提示。默认承载一个 <see cref="string"/>；启用 prompt caching 时会被替换为
    /// <see cref="List{T}"/> of <see cref="AnthropicSystemTextBlock"/>（数组形式）以承载 cache_control 断点。
    /// </summary>
    [JsonPropertyName("system")]
    public object? System { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("tools")]
    public List<AnthropicTool>? Tools { get; set; }
}

/// <summary>
/// Anthropic 消息。
/// </summary>
internal sealed class AnthropicMessage {
    [JsonPropertyName("role")]
    public required string Role { get; set; } // "user" | "assistant"

    [JsonPropertyName("content")]
    public required List<AnthropicContentBlock> Content { get; set; }
}

/// <summary>
/// Anthropic 内容块基类（多态序列化）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnthropicTextBlock), "text")]
[JsonDerivedType(typeof(AnthropicThinkingBlock), "thinking")]
[JsonDerivedType(typeof(AnthropicToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(AnthropicToolResultBlock), "tool_result")]
internal abstract class AnthropicContentBlock {
    /// <summary>
    /// 可选的 prompt caching 断点。非 null 时，Anthropic 会缓存自请求开头到本块（含）的整个前缀。
    /// </summary>
    [JsonPropertyName("cache_control")]
    public AnthropicCacheControl? CacheControl { get; set; }
}

/// <summary>
/// Anthropic prompt caching 断点标记。<c>type</c> 目前固定为 <c>ephemeral</c>。
/// </summary>
internal sealed class AnthropicCacheControl {
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ephemeral";

    public static AnthropicCacheControl Ephemeral { get; } = new();
}

/// <summary>
/// system 提示的 content-block 形式（数组形式的 system），用于承载 cache_control 断点。
/// </summary>
internal sealed class AnthropicSystemTextBlock {
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("cache_control")]
    public AnthropicCacheControl? CacheControl { get; set; }
}

/// <summary>
/// Anthropic tool_result.content 内的内容块基类（当前仅支持 text）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AnthropicToolResultTextContentBlock), "text")]
internal abstract class AnthropicToolResultContentBlock {
}

/// <summary>
/// 文本内容块。
/// </summary>
internal sealed class AnthropicTextBlock : AnthropicContentBlock {
    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

/// <summary>
/// Extended thinking 内容块（assistant 消息中）。
/// </summary>
internal sealed class AnthropicThinkingBlock : AnthropicContentBlock {
    [JsonPropertyName("thinking")]
    public required string Thinking { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

/// <summary>
/// 工具调用块（assistant 消息中）。
/// </summary>
internal sealed class AnthropicToolUseBlock : AnthropicContentBlock {
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("input")]
    public required JsonElement Input { get; set; }
}

/// <summary>
/// 工具结果块（user 消息中）。
/// </summary>
internal sealed class AnthropicToolResultBlock : AnthropicContentBlock {
    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; set; }

    [JsonPropertyName("content")]
    public required List<AnthropicToolResultContentBlock> Content { get; set; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }
}

/// <summary>
/// Anthropic tool_result.content 中的文本内容块。
/// </summary>
internal sealed class AnthropicToolResultTextContentBlock : AnthropicToolResultContentBlock {
    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

/// <summary>
/// 工具定义（暂未使用，预留）。
/// </summary>
internal sealed class AnthropicTool {
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("input_schema")]
    public required JsonElement InputSchema { get; set; }

    /// <summary>
    /// 可选的 prompt caching 断点。放在最后一个 tool 上可缓存整段 tools 定义。
    /// </summary>
    [JsonPropertyName("cache_control")]
    public AnthropicCacheControl? CacheControl { get; set; }
}
