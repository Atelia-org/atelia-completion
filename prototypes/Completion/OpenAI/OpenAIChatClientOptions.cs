using System.Text.Json.Nodes;

namespace Atelia.Completion.OpenAI;

public sealed class OpenAIChatClientOptions {
    public JsonObject? ExtraBody { get; init; }

    public static OpenAIChatClientOptions QwenThinkingDisabled()
        => new() {
            ExtraBody = new JsonObject {
                ["chat_template_kwargs"] = new JsonObject {
                    ["enable_thinking"] = false
                }
            }
        };
}
