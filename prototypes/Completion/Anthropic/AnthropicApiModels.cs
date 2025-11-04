using System.Collections.Generic;
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
    public int MaxTokens { get; set; } = 4096;

    [JsonPropertyName("system")]
    public string? System { get; set; }

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
[JsonDerivedType(typeof(AnthropicToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(AnthropicToolResultBlock), "tool_result")]
internal abstract class AnthropicContentBlock {
}

/// <summary>
/// 文本内容块。
/// </summary>
internal sealed class AnthropicTextBlock : AnthropicContentBlock {
    [JsonPropertyName("text")]
    public required string Text { get; set; }
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
    public required string Content { get; set; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }
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
}
