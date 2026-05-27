using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.Completion.OpenAI;

internal sealed class OpenAIResponsesApiRequest {
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("input")]
    public required List<OpenAIResponsesInputItem> Input { get; set; }

    [JsonPropertyName("tools")]
    public List<OpenAIResponsesTool>? Tools { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("store")]
    public bool Store { get; set; }

    [JsonPropertyName("include")]
    public List<string>? Include { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool ParallelToolCalls { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenAIResponsesMessageItem), "message")]
[JsonDerivedType(typeof(OpenAIResponsesFunctionCallItem), "function_call")]
[JsonDerivedType(typeof(OpenAIResponsesFunctionCallOutputItem), "function_call_output")]
[JsonDerivedType(typeof(OpenAIResponsesReasoningItem), "reasoning")]
internal abstract class OpenAIResponsesInputItem {
}

internal sealed class OpenAIResponsesMessageItem : OpenAIResponsesInputItem {
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public required List<OpenAIResponsesContentItem> Content { get; set; }
}

internal sealed class OpenAIResponsesFunctionCallItem : OpenAIResponsesInputItem {
    [JsonPropertyName("call_id")]
    public required string CallId { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("arguments")]
    public required string Arguments { get; set; }
}

internal sealed class OpenAIResponsesFunctionCallOutputItem : OpenAIResponsesInputItem {
    [JsonPropertyName("call_id")]
    public required string CallId { get; set; }

    [JsonPropertyName("output")]
    public required string Output { get; set; }
}

internal sealed class OpenAIResponsesReasoningItem : OpenAIResponsesInputItem {
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenAIResponsesInputTextContentItem), "input_text")]
[JsonDerivedType(typeof(OpenAIResponsesOutputTextContentItem), "output_text")]
internal abstract class OpenAIResponsesContentItem {
}

internal sealed class OpenAIResponsesInputTextContentItem : OpenAIResponsesContentItem {
    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

internal sealed class OpenAIResponsesOutputTextContentItem : OpenAIResponsesContentItem {
    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

internal sealed class OpenAIResponsesTool {
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public required JsonElement Parameters { get; set; }

    [JsonPropertyName("strict")]
    public bool Strict { get; set; } = true;
}
