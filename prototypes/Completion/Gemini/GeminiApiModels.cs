using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atelia.Completion.Gemini;

internal sealed class GeminiGenerateContentRequest {
    [JsonPropertyName("contents")]
    public required List<GeminiContent> Contents { get; set; }

    [JsonPropertyName("systemInstruction")]
    public GeminiContent? SystemInstruction { get; set; }

    [JsonPropertyName("tools")]
    public List<GeminiTool>? Tools { get; set; }

    [JsonPropertyName("toolConfig")]
    public GeminiToolConfig? ToolConfig { get; set; }
}

internal sealed class GeminiContent {
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("parts")]
    public required List<GeminiPart> Parts { get; set; }
}

internal sealed class GeminiPart {
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("functionCall")]
    public GeminiFunctionCall? FunctionCall { get; set; }

    [JsonPropertyName("functionResponse")]
    public GeminiFunctionResponse? FunctionResponse { get; set; }

    [JsonPropertyName("thoughtSignature")]
    public string? ThoughtSignature { get; set; }
}

internal sealed class GeminiFunctionCall {
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("args")]
    public required JsonElement Args { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class GeminiFunctionResponse {
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("response")]
    public required JsonElement Response { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class GeminiTool {
    [JsonPropertyName("functionDeclarations")]
    public required List<GeminiFunctionDeclaration> FunctionDeclarations { get; set; }
}

internal sealed class GeminiFunctionDeclaration {
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public required JsonElement Parameters { get; set; }
}

internal sealed class GeminiToolConfig {
    [JsonPropertyName("functionCallingConfig")]
    public GeminiFunctionCallingConfig? FunctionCallingConfig { get; set; }
}

internal sealed class GeminiFunctionCallingConfig {
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}

internal sealed class GeminiGenerateContentResponse {
    [JsonPropertyName("candidates")]
    public List<GeminiCandidate>? Candidates { get; set; }
}

internal sealed class GeminiCandidate {
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }

    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }
}
