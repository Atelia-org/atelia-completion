using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesReasoningProjectionTests {
    private static readonly CompletionDescriptor Target = new(
        "chatgpt.com", "openai-codex-responses-v2", "gpt-6-astra");

    public OpenAIResponsesReasoningProjectionTests() => ReasoningBlockCodecs.EnsureRegistered();

    [Theory]
    [InlineData("provider", false)]
    [InlineData("api", true)]
    [InlineData("opaque", false)]
    [InlineData("text", true)]
    public void MixedHistory_OmitsOnlyForeignReasoningAndPreservesCompatibleWire(
        string difference, bool inTail
    ) {
        CompletionDescriptor source = difference switch {
            "provider" => new("different-host", Target.ApiSpecId, Target.Model),
            "api" => new(Target.ProviderId, "openai-responses-v2", Target.Model),
            _ => new("different-host", Target.ApiSpecId, "gpt-5.6-sol")
        };
        const string foreignPayload = "FOREIGN_PAYLOAD_CANARY";
        ActionBlock.ReasoningBlock foreign = difference switch {
            "opaque" => new ActionBlock.OpaqueReasoningBlock(
                "unknown-codec", Encoding.UTF8.GetBytes(foreignPayload), source),
            "text" => new ActionBlock.TextReasoningBlock(foreignPayload, source),
            _ => new OpenAIResponsesReasoningBlock(
                """{"type":"reasoning","encrypted_content":"FOREIGN_PAYLOAD_CANARY"}""", source)
        };
        var native = new OpenAIResponsesReasoningBlock(
            """{"type":"reasoning","id":"rs_current","encrypted_content":"current-payload","summary":[{"type":"summary_text","text":"current-summary"}],"future_field":{"keep":true}}""",
            Target, "current-summary");
        var mixed = new ActionMessage([
            new ActionBlock.Text("first"), foreign, new ActionBlock.Text("second"),
            native, new ActionBlock.Text("third")
        ]);
        var compatible = new ActionMessage([
            new ActionBlock.Text("first"), new ActionBlock.Text("second"),
            native, new ActionBlock.Text("third")
        ]);
        CompletionRequest request = Request(mixed, inTail);
        string originalAction = ActionMessageSerialization.Serialize(mixed);

        OpenAIResponsesApiRequest actual = Project(request);
        OpenAIResponsesApiRequest expected = Project(Request(compatible, inTail));

        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.DoesNotContain(foreignPayload, JsonSerializer.Serialize(actual), StringComparison.Ordinal);
        Assert.Equal(originalAction, ActionMessageSerialization.Serialize(mixed));
        Assert.Same(foreign, mixed.Blocks[1]);
        var replay = Assert.Single(actual.Input.OfType<OpenAIResponsesReasoningItem>());
        Assert.Equal("current-payload", replay.ExtensionData!["encrypted_content"].GetString());
        Assert.True(replay.ExtensionData["future_field"].GetProperty("keep").GetBoolean());
    }

    [Theory]
    [InlineData("openai-codex-responses-v2", "gpt-5.6-sol", "gpt-6-astra", false)]
    [InlineData("openai-codex-responses-v2", "gpt-6-astra", "gpt-5.6-sol", true)]
    [InlineData("openai-codex-responses-v2", "gpt-5.6-sol", "gpt-5.6-luna", true)]
    [InlineData("openai-codex-responses-v2", "gpt-5.6-luna", "gpt-5.6-sol", false)]
    [InlineData("openai-codex-responses-v2", "gpt-5.6-sol", "gpt-5.6-sol", false)]
    [InlineData("openai-responses-v2", "gpt-5.6-sol", "gpt-6-astra", false)]
    [InlineData("openai-responses-v2", "gpt-6-astra", "gpt-5.6-sol", true)]
    [InlineData("openai-responses-v2", "gpt-5.6-sol", "gpt-5.6-luna", true)]
    [InlineData("openai-responses-v2", "gpt-5.6-luna", "gpt-5.6-sol", false)]
    [InlineData("openai-responses-v2", "gpt-5.6-sol", "gpt-5.6-sol", true)]
    public void ModelProvenance_DoesNotFilterOrRewriteNativeReasoning(
        string apiSpecId, string sourceModel, string targetModel, bool inTail
    ) {
        var target = new CompletionDescriptor("same-host", apiSpecId, targetModel);
        var source = new CompletionDescriptor(target.ProviderId, apiSpecId, sourceModel);
        const string raw = """{"type":"reasoning","id":"rs_old","encrypted_content":"opaque-native","summary":[{"type":"summary_text","text":"old summary"}],"future_field":{"array":[1,true,null],"keep":"exact"}}""";
        var reasoning = new OpenAIResponsesReasoningBlock(raw, source, "old summary");
        var action = new ActionMessage([reasoning]);
        string original = ActionMessageSerialization.Serialize(action);
        // Exercise the durable codec too: neither the native bytes nor Origin
        // is migrated to make a historical block eligible for a new model.
        var restored = ActionMessageSerialization.Deserialize(original);
        var request = new CompletionRequest(targetModel,
            new CompletionPromptPrefix("system", CompletionOutputContract.ProviderDefault([]),
                inTail ? [] : [restored]),
            inTail ? [restored] : []);

        var projected = OpenAIResponsesMessageConverter.ConvertToApiRequest(request,
            targetInvocation: target, expectedApiSpecId: apiSpecId);

        var item = Assert.IsType<OpenAIResponsesReasoningItem>(Assert.Single(projected.Input));
        using var expected = JsonDocument.Parse(raw);
        // The request serializes through the base input-item contract, whose
        // polymorphic discriminator supplies the native item's "type" field.
        using var actual = JsonDocument.Parse(JsonSerializer.Serialize<OpenAIResponsesInputItem>(item));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        Assert.Equal(original, ActionMessageSerialization.Serialize(restored));
        var preserved = Assert.IsType<OpenAIResponsesReasoningBlock>(Assert.Single(restored.Blocks));
        Assert.Equal(source, preserved.Origin);
        Assert.Equal(raw, preserved.RawItemJson);
    }

    [Fact]
    public void CrossModelReasoning_PreservesToolDependencyAcrossPrefixTail() {
        var old = new OpenAIResponsesReasoningBlock(
            """{"type":"reasoning","encrypted_content":"old-payload"}""",
            new CompletionDescriptor(Target.ProviderId, Target.ApiSpecId, "gpt-5.6-sol"));
        var action = new ActionMessage([
            old, new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}"))
        ]);
        var results = new ToolResultsMessage(null, [
            ToolResult.FromText("lookup", "call-1", ToolExecutionStatus.Success, "found")
        ]);
        var request = new CompletionRequest(Target.Model,
            new CompletionPromptPrefix("", CompletionOutputContract.ProviderDefault([]), [action]),
            [results, new ObservationMessage("continue")]);

        Assert.Collection(Project(request).Input,
            item => Assert.Equal("old-payload", Assert.IsType<OpenAIResponsesReasoningItem>(item)
                .ExtensionData!["encrypted_content"].GetString()),
            item => Assert.Equal("call-1", Assert.IsType<OpenAIResponsesFunctionCallItem>(item).CallId),
            item => Assert.Equal("found", Assert.IsType<OpenAIResponsesFunctionCallOutputItem>(item).Output),
            item => Assert.Equal("user", Assert.IsType<OpenAIResponsesMessageItem>(item).Role));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignReasoningOnlyAction_DoesNotHideBrokenToolAdjacency(bool appendResults) {
        var toolAction = new ActionMessage([
            new ActionBlock.ToolCall(new RawToolCall("lookup", "call-1", "{}"))
        ]);
        var foreignOnly = new ActionMessage([
            new ActionBlock.TextReasoningBlock("private",
                new CompletionDescriptor("other-host", Target.ApiSpecId, "old-model"))
        ]);
        var results = new ToolResultsMessage(null, [
            ToolResult.FromText("lookup", "call-1", ToolExecutionStatus.Success, "found")
        ]);
        var request = new CompletionRequest(Target.Model,
            new CompletionPromptPrefix("", CompletionOutputContract.ProviderDefault([]), [toolAction]),
            appendResults ? [foreignOnly, results] : [foreignOnly]);

        Assert.Throws<InvalidOperationException>(() => Project(request));
    }

    [Fact]
    public void OriginallyEmptyAction_IsStillRejected() {
        Assert.Throws<InvalidOperationException>(() => Project(Request(new ActionMessage([]), false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossModelUnsupportedCarrier_IsNotSilentlyOmitted(bool opaque) {
        var source = new CompletionDescriptor(Target.ProviderId, Target.ApiSpecId, "old-model");
        ActionBlock.ReasoningBlock block = opaque
            ? new ActionBlock.OpaqueReasoningBlock("unregistered", new byte[] { 1, 2 }, source)
            : new ActionBlock.TextReasoningBlock("private", source);

        var failure = Assert.Throws<CompletionRequestRejectedException>(() =>
            Project(Request(new ActionMessage([block]), false)));

        Assert.Equal("openai.responses.invalid-reasoning-replay", failure.Termination.ProviderReason);
        Assert.Null(failure.InnerException);
    }

    private static CompletionRequest Request(ActionMessage action, bool inTail) => new(
        Target.Model,
        new CompletionPromptPrefix("system", CompletionOutputContract.ProviderDefault([]),
            inTail ? [] : [action]),
        inTail ? [action] : []);

    private static OpenAIResponsesApiRequest Project(CompletionRequest request) =>
        OpenAIResponsesMessageConverter.ConvertToApiRequest(request,
            targetInvocation: Target, expectedApiSpecId: Target.ApiSpecId);
}
