using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.Gemini;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.Completion.Tests.Abstractions;

public sealed class ActionMessageSerializationTests {
    private static readonly CompletionDescriptor Invocation = new(
        "provider",
        "spec",
        "model"
    );

    [Fact]
    public void TextReasoningBlock_RoundTripsThroughDefaultRegistry() {
        var message = new ActionMessage(
            new ActionBlock[] {
                new ActionBlock.Text("visible"),
                new ActionBlock.TextReasoningBlock("think", Invocation, "debug"),
                new ActionBlock.ToolCall(new RawToolCall("tool", "call-1", "{\"x\":1}"))
            }
        );

        var json = ActionMessageSerialization.Serialize(message);
        var restored = ActionMessageSerialization.Deserialize(json);

        Assert.Equal(3, restored.Blocks.Count);
        Assert.Equal("visible", Assert.IsType<ActionBlock.Text>(restored.Blocks[0]).Content);

        var reasoning = Assert.IsType<ActionBlock.TextReasoningBlock>(restored.Blocks[1]);
        Assert.Equal("think", reasoning.Content);
        Assert.Equal("debug", reasoning.PlainTextForDebug);
        Assert.Equal(Invocation, reasoning.Origin);

        var toolCall = Assert.IsType<ActionBlock.ToolCall>(restored.Blocks[2]).Call;
        Assert.Equal("tool", toolCall.ToolName);
        Assert.Equal("call-1", toolCall.ToolCallId);
        Assert.Equal("{\"x\":1}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public void ProviderNativeReasoningBlocks_RoundTripThroughRegisteredCodecs() {
        var registry = ReasoningBlockCodecRegistry.CreateDefault();
        ReasoningBlockCodecs.RegisterAll(registry);

        var anthropicPayload = Encoding.UTF8.GetBytes("{\"type\":\"thinking\",\"thinking\":\"a\",\"signature\":\"sig\"}");
        var geminiPayload = Encoding.UTF8.GetBytes("{\"role\":\"model\",\"parts\":[{\"text\":\"hello\",\"thoughtSignature\":\"sig\"}]}");
        const string openAiResponsesJson = "{\"type\":\"reasoning\",\"id\":\"rs_1\"}";

        var message = new ActionMessage(
            new ActionBlock[] {
                new AnthropicReasoningBlock(anthropicPayload, Invocation, "anthropic-debug"),
                new GeminiReplayBlock(geminiPayload, Invocation, "gemini-debug"),
                new OpenAIChatReasoningBlock("chat-reasoning", Invocation, "chat-debug"),
                new OpenAIResponsesReasoningBlock(openAiResponsesJson, Invocation, "responses-debug")
            }
        );

        var json = ActionMessageSerialization.Serialize(message, registry);
        var restored = ActionMessageSerialization.Deserialize(json, registry);

        var anthropic = Assert.IsType<AnthropicReasoningBlock>(restored.Blocks[0]);
        Assert.Equal(anthropicPayload, anthropic.OpaquePayload.ToArray());
        Assert.Equal("anthropic-debug", anthropic.PlainTextForDebug);
        Assert.Equal(Invocation, anthropic.Origin);

        var gemini = Assert.IsType<GeminiReplayBlock>(restored.Blocks[1]);
        Assert.Equal(geminiPayload, gemini.OpaquePayload.ToArray());
        Assert.Equal("gemini-debug", gemini.PlainTextForDebug);
        Assert.Equal(Invocation, gemini.Origin);

        var chat = Assert.IsType<OpenAIChatReasoningBlock>(restored.Blocks[2]);
        Assert.Equal("chat-reasoning", chat.Content);
        Assert.Equal("chat-debug", chat.PlainTextForDebug);
        Assert.Equal(Invocation, chat.Origin);

        var responses = Assert.IsType<OpenAIResponsesReasoningBlock>(restored.Blocks[3]);
        Assert.Equal(openAiResponsesJson, responses.RawItemJson);
        Assert.Equal("responses-debug", responses.PlainTextForDebug);
        Assert.Equal(Invocation, responses.Origin);
    }

    [Fact]
    public void UnknownReasoningCodec_DecodesToTextReasoningFallback() {
        var serialized = new[] {
            new SerializedActionBlock(
                ActionMessageSerialization.BlockKindReasoning,
                null,
                null,
                null,
                null,
                SerializedReasoningBlock.Create(
                    "unknown.codec.v1",
                    Invocation,
                    Encoding.UTF8.GetBytes("opaque"),
                    "fallback text"
                )
            )
        };

        var restored = ActionMessageSerialization.FromSerializedBlocks(serialized);

        var reasoning = Assert.IsType<ActionBlock.TextReasoningBlock>(Assert.Single(restored));
        Assert.Equal("fallback text", reasoning.Content);
        Assert.Equal("fallback text", reasoning.PlainTextForDebug);
        Assert.Equal(Invocation, reasoning.Origin);
    }
}
