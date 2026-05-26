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
}
