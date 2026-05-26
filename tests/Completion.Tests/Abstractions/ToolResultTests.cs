using Xunit;

namespace Atelia.Completion.Abstractions.Tests;

public sealed class ToolResultTests {
    [Fact]
    public void Constructor_CopiesIncomingBlocks_ToPreserveSnapshotSemantics() {
        var sourceBlocks = new List<ToolResultBlock> {
            new ToolResultBlock.Text("alpha")
        };

        var result = new ToolResult("search", "call-1", ToolExecutionStatus.Success, sourceBlocks);

        sourceBlocks.Add(new ToolResultBlock.Text("omega"));

        Assert.Collection(
            result.Blocks,
            block => Assert.Equal("alpha", Assert.IsType<ToolResultBlock.Text>(block).Content)
        );
        Assert.Equal("alpha", result.GetFlattenedText());
    }

    [Fact]
    public void Constructor_RejectsNullBlocks() {
        Assert.Throws<ArgumentNullException>(
            () => new ToolResult("search", "call-1", ToolExecutionStatus.Success, null!)
        );
    }

    [Fact]
    public void FromText_CreatesSingleTextBlock_AndFlattenedTextMatches() {
        var result = ToolResult.FromText("search", "call-1", ToolExecutionStatus.Failed, "bad");

        Assert.Equal("search", result.ToolName);
        Assert.Equal("call-1", result.ToolCallId);
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        var block = Assert.Single(result.Blocks);
        Assert.Equal("bad", Assert.IsType<ToolResultBlock.Text>(block).Content);
        Assert.Equal("bad", result.GetFlattenedText());
    }
}
