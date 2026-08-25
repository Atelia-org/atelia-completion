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

        Assert.Contains("\"plainTextForDebug\":\"debug\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"plainText\":", json, StringComparison.Ordinal);
        Assert.Equal(3, restored.Blocks.Count);
        Assert.Equal("visible", Assert.IsType<ActionBlock.Text>(restored.Blocks[0]).Content);

        var reasoning = Assert.IsType<ActionBlock.TextReasoningBlock>(restored.Blocks[1]);
        Assert.Equal("think", reasoning.Content);
        Assert.Equal("debug", reasoning.PlainText);
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
        const string openAiResponsesJson = "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"responses-debug\"}]}";

        var message = new ActionMessage(
            new ActionBlock[] {
                new AnthropicReasoningBlock(anthropicPayload, Invocation, "a"),
                new GeminiReplayBlock(geminiPayload, Invocation),
                new OpenAIChatReasoningBlock("chat-reasoning", Invocation),
                new OpenAIResponsesReasoningBlock(openAiResponsesJson, Invocation, "responses-debug")
            }
        );

        var json = ActionMessageSerialization.Serialize(message, registry);
        var restored = ActionMessageSerialization.Deserialize(json, registry);

        var anthropic = Assert.IsType<AnthropicReasoningBlock>(restored.Blocks[0]);
        Assert.Equal(anthropicPayload, anthropic.OpaquePayload.ToArray());
        Assert.Equal("a", anthropic.PlainText);
        Assert.Equal(Invocation, anthropic.Origin);

        var gemini = Assert.IsType<GeminiReplayBlock>(restored.Blocks[1]);
        Assert.Equal(geminiPayload, gemini.OpaquePayload.ToArray());
        Assert.Null(gemini.PlainText);
        Assert.Equal(Invocation, gemini.Origin);

        var chat = Assert.IsType<OpenAIChatReasoningBlock>(restored.Blocks[2]);
        Assert.Equal("chat-reasoning", chat.Content);
        Assert.Equal("chat-reasoning", chat.PlainText);
        Assert.Equal(Invocation, chat.Origin);

        var responses = Assert.IsType<OpenAIResponsesReasoningBlock>(restored.Blocks[3]);
        Assert.Equal(openAiResponsesJson, responses.RawItemJson);
        Assert.Equal("responses-debug", responses.PlainText);
        Assert.Equal(Invocation, responses.Origin);
    }

    [Fact]
    public void AnthropicReasoningBlock_CopiesCallerOwnedPayload() {
        byte[] callerOwned = [1, 2, 3];
        var block = new AnthropicReasoningBlock(
            callerOwned,
            Invocation,
            "reason"
        );

        callerOwned[0] = 99;

        Assert.Equal([1, 2, 3], block.OpaquePayload.ToArray());
    }

    [Fact]
    public void GeminiReplayBlock_CopiesCallerOwnedPayload() {
        byte[] callerOwned = [4, 5, 6];
        var block = new GeminiReplayBlock(callerOwned, Invocation);

        callerOwned[0] = 99;

        Assert.Equal([4, 5, 6], block.OpaquePayload.ToArray());
    }

    [Fact]
    public void UnknownReasoningCodec_RoundTripsExactlyThroughOpaqueCarrier() {
        byte[] callerOwnedPayload = [0, 1, 2, 254, 255];
        var unknown = new SerializedReasoningBlock(
            "unknown.codec.v1",
            Invocation.ProviderId,
            Invocation.ApiSpecId,
            Invocation.Model,
            callerOwnedPayload,
            "debug-only text"
        );
        var serialized = new[] {
            new SerializedActionBlock(
                ActionMessageSerialization.BlockKindReasoning,
                null,
                null,
                null,
                null,
                unknown
            )
        };

        var restored = ActionMessageSerialization.FromSerializedBlocks(serialized);
        callerOwnedPayload[0] = 99;

        var reasoning = Assert.IsType<ActionBlock.OpaqueReasoningBlock>(Assert.Single(restored));
        Assert.Equal("unknown.codec.v1", reasoning.CodecId);
        Assert.Equal([0, 1, 2, 254, 255], reasoning.OpaquePayload.ToArray());
        Assert.Equal("debug-only text", reasoning.PlainText);
        Assert.Equal(Invocation, reasoning.Origin);

        SerializedReasoningBlock roundTripped = Assert.Single(
            ActionMessageSerialization.ToSerializedBlocks(restored)
        ).Reasoning!;
        Assert.Equal(unknown.CodecId, roundTripped.CodecId);
        Assert.Equal(unknown.OriginProviderId, roundTripped.OriginProviderId);
        Assert.Equal(unknown.OriginApiSpecId, roundTripped.OriginApiSpecId);
        Assert.Equal(unknown.OriginModel, roundTripped.OriginModel);
        Assert.Equal([0, 1, 2, 254, 255], roundTripped.Payload);
        Assert.Equal(unknown.PlainText, roundTripped.PlainText);

        roundTripped.Payload[1] = 88;
        SerializedReasoningBlock encodedAgain = Assert.Single(
            ActionMessageSerialization.ToSerializedBlocks(restored)
        ).Reasoning!;
        Assert.Equal([0, 1, 2, 254, 255], encodedAgain.Payload);
    }

    [Fact]
    public void OpaqueReasoningBlock_EqualityUsesPayloadBytes() {
        var left = new ActionBlock.OpaqueReasoningBlock(
            "unknown.codec.v1",
            new byte[] { 1, 2, 3 },
            Invocation,
            "debug"
        );
        var equal = new ActionBlock.OpaqueReasoningBlock(
            "unknown.codec.v1",
            new byte[] { 1, 2, 3 },
            Invocation,
            "debug"
        );
        var differentPayload = new ActionBlock.OpaqueReasoningBlock(
            "unknown.codec.v1",
            new byte[] { 1, 2, 4 },
            Invocation,
            "debug"
        );
        var differentCodec = new ActionBlock.OpaqueReasoningBlock(
            "unknown.codec.v2",
            new byte[] { 1, 2, 3 },
            Invocation,
            "debug"
        );
        var differentOrigin = new ActionBlock.OpaqueReasoningBlock(
            "unknown.codec.v1",
            new byte[] { 1, 2, 3 },
            new CompletionDescriptor("provider", "spec", "other-model"),
            "debug"
        );
        var differentPlainText = new ActionBlock.OpaqueReasoningBlock(
            "unknown.codec.v1",
            new byte[] { 1, 2, 3 },
            Invocation,
            "other-debug"
        );

        Assert.Equal(left, equal);
        Assert.True(left == equal);
        Assert.Equal(left.GetHashCode(), equal.GetHashCode());
        Assert.NotEqual(left, differentPayload);
        Assert.True(left != differentPayload);
        Assert.NotEqual(left, differentCodec);
        Assert.NotEqual(left, differentOrigin);
        Assert.NotEqual(left, differentPlainText);
    }

    [Fact]
    public void OpaqueReasoningBlock_SerializationRoundTripPreservesValueEquality() {
        var original = new ActionBlock.OpaqueReasoningBlock(
            "unknown.codec.v1",
            new byte[] { 0, 127, 128, 255 },
            Invocation,
            "debug"
        );
        var message = new ActionMessage([original]);

        ActionMessage restored = ActionMessageSerialization.Deserialize(
            ActionMessageSerialization.Serialize(message)
        );
        var roundTripped = Assert.IsType<ActionBlock.OpaqueReasoningBlock>(
            Assert.Single(restored.Blocks)
        );

        Assert.Equal(original, roundTripped);
        Assert.Equal(original.GetHashCode(), roundTripped.GetHashCode());
    }

    [Fact]
    public void OpaqueReasoningBlock_PublicPayloadSnapshotCannotMutateInstance() {
        var block = new ActionBlock.OpaqueReasoningBlock(
            "unknown.codec.v1",
            new byte[] { 10, 20, 30 },
            Invocation,
            "debug"
        );
        int originalHash = block.GetHashCode();
        ReadOnlyMemory<byte> publicSnapshot = block.OpaquePayload;

        Assert.True(
            System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                publicSnapshot,
                out ArraySegment<byte> extracted
            )
        );
        extracted.Array![extracted.Offset + 1] = 99;

        Assert.Equal([10, 20, 30], block.OpaquePayload.ToArray());
        Assert.Equal(originalHash, block.GetHashCode());
        SerializedReasoningBlock serialized = Assert.Single(
            ActionMessageSerialization.ToSerializedBlocks([block])
        ).Reasoning!;
        Assert.Equal([10, 20, 30], serialized.Payload);
    }

    [Fact]
    public void ProviderReasoningCodec_RejectsForgedPlainTextView() {
        var registry = ReasoningBlockCodecRegistry.CreateDefault();
        ReasoningBlockCodecs.RegisterAll(registry);
        var message = new ActionMessage([
            new OpenAIChatReasoningBlock(
                "authoritative",
                Invocation,
                "forged"
            )
        ]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => ActionMessageSerialization.Serialize(message, registry)
        );

        Assert.Contains("PlainText", exception.Message, StringComparison.Ordinal);
    }
}
