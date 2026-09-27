using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tests.Abstractions;

public sealed class TextToolContractTests {
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    [InlineData("*** Begin Patch\n+你好 ${x} \\ \"\n*** End Patch\n")]
    public void ActionSerialization_RoundTripsRawTextAndKind(string text) {
        var call = RawToolCall.FromText("patch", "c1", text);
        var message = new ActionMessage([new ActionBlock.ToolCall(call)]);
        var json = ActionMessageSerialization.Serialize(message);
        Assert.Contains("text-tool-call", json);
        var restored = Assert.Single(ActionMessageSerialization.Deserialize(json).ToolCalls);
        Assert.Equal(call, restored);
        Assert.Equal(ToolInputKind.Text, restored.InputKind);
        Assert.Equal(text, restored.RawInput);
        Assert.Throws<InvalidOperationException>(() => restored.RawArgumentsJson);
    }

    [Fact]
    public void JsonHistory_WireShapeRemainsCompatible() {
        var call = new RawToolCall("tool", "c1", "{}");
        var json = ActionMessageSerialization.Serialize(new ActionMessage([new ActionBlock.ToolCall(call)]));
        Assert.DoesNotContain("inputKind", json);
        Assert.DoesNotContain("rawInput", json);
        var restored = Assert.Single(ActionMessageSerialization.Deserialize(json).ToolCalls);
        Assert.Equal(call, restored);
        Assert.Equal(ToolInputKind.JsonObject, restored.InputKind);
    }

    [Theory]
    [InlineData("[{\"kind\":\"text-tool-call\",\"toolName\":\"t\",\"toolCallId\":\"c\",\"inputKind\":\"text\"}]")]
    [InlineData("[{\"kind\":\"text-tool-call\",\"toolName\":\"t\",\"toolCallId\":\"c\",\"inputKind\":\"text\",\"rawInput\":\"x\",\"rawArgumentsJson\":\"{}\"}]")]
    public void InvalidTextHistory_RejectsMissingOrConflictingPayload(string json) {
        Assert.Throws<System.IO.InvalidDataException>(() => ActionMessageSerialization.Deserialize(json));
    }

    [Fact]
    public void TextHistory_UsesKindThatOldReadersRejectAndRejectsConflictingKind() {
        var dto = Assert.Single(ActionMessageSerialization.ToSerializedBlocks([
            new ActionBlock.ToolCall(RawToolCall.FromText("text", "id", "input"))]));
        Assert.NotEqual(ActionMessageSerialization.BlockKindToolCall, dto.Kind);
        Assert.Throws<InvalidDataException>(() => ActionMessageSerialization.FromSerializedBlocks([
            dto with { Kind = ActionMessageSerialization.BlockKindToolCall }]));
    }

    [Fact]
    public void Fingerprint_DistinguishesInputKindsAndGrammar() {
        ToolDefinition[] tools = [
            new("same", "Description", new ToolSchema.Object()),
            ToolDefinition.FromText("same", "Description"),
            ToolDefinition.FromText("same", "Description", ToolTextFormat.Grammar("regex", ".+")),
            ToolDefinition.FromText("same", "Description", ToolTextFormat.Grammar("regex", ".*")),
            ToolDefinition.FromText("same", "Description", ToolTextFormat.Grammar("lark", "start: /.+/"))
        ];
        var hashes = tools.Select(tool => CompletionOutputContract.ProviderDefault([tool]).SemanticFingerprint).ToArray();
        Assert.Equal(tools.Length, hashes.Distinct().Count());
        Assert.Equal(hashes[1], CompletionOutputContract.ProviderDefault([ToolDefinition.FromText("same", "Description")]).SemanticFingerprint);
    }

    [Fact]
    public void TextRecords_CanBeFormattedWithoutInvokingJsonGetters() {
        var call = RawToolCall.FromText("text", "id", "private payload");
        Assert.Contains("Text", call.ToString());
        Assert.DoesNotContain("private payload", call.ToString());
        Assert.Contains("Text", new ActionBlock.ToolCall(call).ToString());
        Assert.Contains("Text", ToolDefinition.FromText("text", "Description").ToString());
    }

    [Fact]
    public void JsonCall_RecordUpdateKeepsSingleInputAuthority() {
        var call = new RawToolCall("tool", "id", "{}");
        var changed = call with { RawArgumentsJson = "{\"value\":1}" };
        Assert.Equal("{}", call.RawInput);
        Assert.Equal("{\"value\":1}", changed.RawInput);
        Assert.Equal(changed.RawInput, changed.RawArgumentsJson);
        var text = RawToolCall.FromText("text", "id", "raw");
        Assert.Throws<InvalidOperationException>(() => text with { RawArgumentsJson = "{}" });
    }

    [Theory]
    [InlineData("invalid", "pattern")]
    [InlineData("lark", " ")]
    public void Grammar_RejectsInvalidDeclaration(string syntax, string definition) {
        Assert.Throws<ArgumentException>(() => ToolTextFormat.Grammar(syntax, definition));
    }
}
