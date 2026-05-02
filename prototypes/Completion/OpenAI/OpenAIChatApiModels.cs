using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.Completion.OpenAI;

internal sealed class OpenAIChatApiRequest {
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<OpenAIChatMessage> Messages { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("tools")]
    public List<OpenAIChatTool>? Tools { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class OpenAIChatMessage {
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenAIChatToolCall>? ToolCalls { get; set; }
}

internal sealed class OpenAIChatTool {
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required OpenAIChatToolDefinition Function { get; set; }
}

internal sealed class OpenAIChatToolDefinition {
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public required JsonElement Parameters { get; set; }
}

internal sealed class OpenAIChatToolCall {
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required OpenAIChatFunctionCall Function { get; set; }
}

internal sealed class OpenAIChatFunctionCall {
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("arguments")]
    public required string Arguments { get; set; }
}
