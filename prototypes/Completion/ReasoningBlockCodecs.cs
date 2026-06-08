using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.Gemini;
using Atelia.Completion.OpenAI;

namespace Atelia.Completion;

/// <summary>
/// 注册 Completion provider 当前支持的 provider-native reasoning/replay block codecs。
/// </summary>
public static class ReasoningBlockCodecs {
    public static void EnsureRegistered(ReasoningBlockCodecRegistry? registry = null) {
        RegisterAll(registry ?? ReasoningBlockCodecRegistry.Shared);
    }

    public static void RegisterAll(ReasoningBlockCodecRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new AnthropicReasoningBlockCodec());
        registry.Register(new GeminiReplayBlockCodec());
        registry.Register(new OpenAIChatReasoningBlockCodec());
        registry.Register(new OpenAIResponsesReasoningBlockCodec());
    }

    private sealed class AnthropicReasoningBlockCodec : IReasoningBlockCodec {
        public string CodecId => "atelia.anthropic.reasoning.v1";

        public bool CanEncode(ActionBlock.ReasoningBlock block)
            => block is AnthropicReasoningBlock;

        public SerializedReasoningBlock Encode(ActionBlock.ReasoningBlock block) {
            if (block is not AnthropicReasoningBlock anthropicBlock) { throw new ArgumentException("Codec can only encode AnthropicReasoningBlock.", nameof(block)); }

            return SerializedReasoningBlock.Create(
                CodecId,
                anthropicBlock.Origin,
                anthropicBlock.OpaquePayload.ToArray(),
                anthropicBlock.PlainTextForDebug
            );
        }

        public ActionBlock.ReasoningBlock Decode(SerializedReasoningBlock serialized)
            => new AnthropicReasoningBlock(
                serialized.Payload.ToArray(),
                serialized.ToOrigin(),
                serialized.PlainTextForDebug
            );
    }

    private sealed class GeminiReplayBlockCodec : IReasoningBlockCodec {
        public string CodecId => "atelia.gemini.replay.v1";

        public bool CanEncode(ActionBlock.ReasoningBlock block)
            => block is GeminiReplayBlock;

        public SerializedReasoningBlock Encode(ActionBlock.ReasoningBlock block) {
            if (block is not GeminiReplayBlock geminiBlock) { throw new ArgumentException("Codec can only encode GeminiReplayBlock.", nameof(block)); }

            return SerializedReasoningBlock.Create(
                CodecId,
                geminiBlock.Origin,
                geminiBlock.OpaquePayload.ToArray(),
                geminiBlock.PlainTextForDebug
            );
        }

        public ActionBlock.ReasoningBlock Decode(SerializedReasoningBlock serialized)
            => new GeminiReplayBlock(
                serialized.Payload.ToArray(),
                serialized.ToOrigin(),
                serialized.PlainTextForDebug
            );
    }

    private sealed class OpenAIChatReasoningBlockCodec : IReasoningBlockCodec {
        public string CodecId => "atelia.openai-chat.reasoning-content.v1";

        public bool CanEncode(ActionBlock.ReasoningBlock block)
            => block is OpenAIChatReasoningBlock;

        public SerializedReasoningBlock Encode(ActionBlock.ReasoningBlock block) {
            if (block is not OpenAIChatReasoningBlock openAiBlock) { throw new ArgumentException("Codec can only encode OpenAIChatReasoningBlock.", nameof(block)); }

            return SerializedReasoningBlock.Create(
                CodecId,
                openAiBlock.Origin,
                Encoding.UTF8.GetBytes(openAiBlock.Content),
                openAiBlock.PlainTextForDebug
            );
        }

        public ActionBlock.ReasoningBlock Decode(SerializedReasoningBlock serialized) {
            var content = Encoding.UTF8.GetString(serialized.Payload);
            return new OpenAIChatReasoningBlock(
                content,
                serialized.ToOrigin(),
                serialized.PlainTextForDebug ?? content
            );
        }
    }

    private sealed class OpenAIResponsesReasoningBlockCodec : IReasoningBlockCodec {
        public string CodecId => "atelia.openai-responses.reasoning-item-json.v1";

        public bool CanEncode(ActionBlock.ReasoningBlock block)
            => block is OpenAIResponsesReasoningBlock;

        public SerializedReasoningBlock Encode(ActionBlock.ReasoningBlock block) {
            if (block is not OpenAIResponsesReasoningBlock responsesBlock) { throw new ArgumentException("Codec can only encode OpenAIResponsesReasoningBlock.", nameof(block)); }

            return SerializedReasoningBlock.Create(
                CodecId,
                responsesBlock.Origin,
                Encoding.UTF8.GetBytes(responsesBlock.RawItemJson),
                responsesBlock.PlainTextForDebug
            );
        }

        public ActionBlock.ReasoningBlock Decode(SerializedReasoningBlock serialized) {
            var rawItemJson = Encoding.UTF8.GetString(serialized.Payload);
            return new OpenAIResponsesReasoningBlock(
                rawItemJson,
                serialized.ToOrigin(),
                serialized.PlainTextForDebug
            );
        }
    }
}
