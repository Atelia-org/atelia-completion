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

    [JsonPropertyName("stream_options")]
    public OpenAIChatStreamOptions? StreamOptions { get; set; }

    [JsonPropertyName("tools")]
    public List<OpenAIChatTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public object? ToolChoice { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("thinking")]
    public OpenAIChatThinkingConfig? Thinking { get; set; }

    [JsonPropertyName("chat_template_kwargs")]
    public OpenAIChatTemplateKwargs? ChatTemplateKwargs { get; set; }
}

internal sealed class OpenAIChatThinkingConfig {
    [JsonPropertyName("type")]
    public required string Type { get; set; }
}

internal sealed class OpenAIChatTemplateKwargs {
    [JsonPropertyName("enable_thinking")]
    public bool EnableThinking { get; set; }
}

internal sealed class OpenAIChatStreamOptions {
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; set; }
}

internal sealed class OpenAIChatNamedToolChoice {
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required OpenAIChatNamedFunctionChoice Function { get; set; }
}

internal sealed class OpenAIChatNamedFunctionChoice {
    [JsonPropertyName("name")]
    public required string Name { get; set; }
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
