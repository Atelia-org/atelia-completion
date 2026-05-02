using Xunit;

namespace Atelia.Completion.Abstractions.Tests;

public sealed class CompletionResultTests {
    private static readonly CompletionDescriptor Descriptor = new("provider", "spec", "model");

    [Fact]
    public void Constructor_CopiesIncomingLists_ToPreserveSnapshotSemantics() {
        var sourceBlocks = new List<ActionBlock> {
            new ActionBlock.Text("alpha"),
            new ActionBlock.ToolCall(new ParsedToolCall("tool.a", "call-1", null, new Dictionary<string, object?>(), null, null))
        };
        var sourceErrors = new List<string> { "boom-1" };

        var message = new ActionMessage(sourceBlocks);
        var result = new CompletionResult(message, Descriptor, sourceErrors);

        sourceBlocks.Add(new ActionBlock.Text("omega"));
        sourceErrors.Add("boom-2");

        Assert.Collection(
            result.Message.Blocks,
            block => Assert.Equal("alpha", Assert.IsType<ActionBlock.Text>(block).Content),
            block => Assert.Equal("call-1", Assert.IsType<ActionBlock.ToolCall>(block).Call.ToolCallId)
        );
        Assert.Equal("alpha", result.Message.GetFlattenedText());
        Assert.Collection(result.Message.ToolCalls, call => Assert.Equal("call-1", call.ToolCallId));
        Assert.Equal(new[] { "boom-1" }, result.Errors);
    }

    [Fact]
    public void CompletionResult_RemainsEnvelope_NotActionMessage() {
        var result = new CompletionResult(
            message: new ActionMessage(new[] { new ActionBlock.Text("alpha") }),
            invocation: Descriptor
        );

        Assert.IsNotAssignableFrom<IHistoryMessage>(result);
    }

    [Fact]
    public void WithExpression_AlsoFreezesAssignedLists() {
        var baseResult = new CompletionResult(
            message: new ActionMessage(new[] { new ActionBlock.Text("seed") }),
            invocation: Descriptor
        );
        var replacementBlocks = new List<ActionBlock> { new ActionBlock.Text("beta") };
        var replacementErrors = new List<string> { "warn-1" };

        var cloned = baseResult with {
            Message = new ActionMessage(replacementBlocks),
            Errors = replacementErrors,
        };

        replacementBlocks.Add(new ActionBlock.Text("gamma"));
        replacementErrors.Add("warn-2");

        Assert.Collection(
            cloned.Message.Blocks,
            block => Assert.Equal("beta", Assert.IsType<ActionBlock.Text>(block).Content)
        );
        Assert.Equal("beta", cloned.Message.GetFlattenedText());
        Assert.Equal(new[] { "warn-1" }, cloned.Errors);
    }
}
