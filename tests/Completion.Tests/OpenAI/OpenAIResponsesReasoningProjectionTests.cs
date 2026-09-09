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
    [InlineData("model", false)]
    [InlineData("model", true)]
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
            _ => new(Target.ProviderId, Target.ApiSpecId, "gpt-5.6-sol")
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

    [Fact]
    public void ForeignReasoning_DoesNotChangeToolDependencyAcrossPrefixTail() {
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
                new CompletionDescriptor(Target.ProviderId, Target.ApiSpecId, "old-model"))
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
    public void SameOriginUnsupportedCarrier_IsNotSilentlyOmitted(bool opaque) {
        ActionBlock.ReasoningBlock block = opaque
            ? new ActionBlock.OpaqueReasoningBlock("unregistered", new byte[] { 1, 2 }, Target)
            : new ActionBlock.TextReasoningBlock("private", Target);

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
