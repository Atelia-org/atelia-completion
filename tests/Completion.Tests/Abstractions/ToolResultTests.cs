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
    public void Constructor_RejectsNullBlockElement() {
        var blocks = new ToolResultBlock[] { null! };

        var exception = Assert.Throws<ArgumentException>(
            () => new ToolResult("search", "call-1", ToolExecutionStatus.Success, blocks)
        );

        Assert.Contains("cannot contain null elements", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public void ToolResultsMessage_Constructor_CopiesIncomingResults() {
        var sourceResults = new List<ToolResult> {
            ToolResult.FromText("search", "call-1", ToolExecutionStatus.Success, "alpha")
        };

        var message = new ToolResultsMessage(content: "observed", results: sourceResults);

        sourceResults.Add(ToolResult.FromText("search", "call-2", ToolExecutionStatus.Success, "omega"));

        var only = Assert.Single(message.Results);
        Assert.Equal("call-1", only.ToolCallId);
        Assert.Equal("observed", message.Content);
    }

    [Fact]
    public void ToolResultsMessage_Constructor_RejectsNullResultElement() {
        var results = new ToolResult[] { null! };

        var exception = Assert.Throws<ArgumentException>(
            () => new ToolResultsMessage(content: null, results: results)
        );

        Assert.Contains("cannot contain null elements", exception.Message, StringComparison.Ordinal);
    }
}
