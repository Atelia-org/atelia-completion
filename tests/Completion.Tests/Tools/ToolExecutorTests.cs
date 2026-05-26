using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tools.Tests;

public sealed class ToolExecutorTests {
    [Fact]
    public void VisibleToolDefinitions_UsesSessionPolicy() {
        var alpha = new RecordingTool("alpha");
        var beta = new RecordingTool("beta");
        var registry = new ToolRegistry(new ITool[] { alpha, beta });
        var session = new ToolSessionState(new ToolAccessPolicy(hiddenToolNames: new[] { "BETA" }));
        var executor = new ToolExecutor(registry, session);

        var visibleDefinitions = executor.VisibleToolDefinitions;

        var visibleDefinition = Assert.Single(visibleDefinitions);
        Assert.Equal("alpha", visibleDefinition.Name);
        Assert.True(executor.TryGetTool("ALPHA", out var visibleTool));
        Assert.Same(alpha, visibleTool);
        Assert.False(executor.TryGetTool("beta", out _));
    }

    [Fact]
    public async Task ExecuteAsync_UsesSessionSequenceAndContextItems() {
        var tool = new RecordingTool("alpha");
        var registry = new ToolRegistry(new ITool[] { tool });
        var session = new ToolSessionState(items: new Dictionary<string, object?> { ["scope"] = "session-scope" });
        var executor = new ToolExecutor(registry, session);

        var first = await executor.ExecuteAsync(new RawToolCall("alpha", "call-1", "{}"), CancellationToken.None);
        var second = await executor.ExecuteAsync(new RawToolCall("alpha", "call-2", "{}"), CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Success, first.ExecuteResult.Status);
        AssertSingleTextBlock(first.ExecuteResult.Blocks, "sequence=1 scope=session-scope");
        Assert.Equal("sequence=1 scope=session-scope", first.ExecuteResult.GetFlattenedText());
        Assert.Equal(ToolExecutionStatus.Success, second.ExecuteResult.Status);
        AssertSingleTextBlock(second.ExecuteResult.Blocks, "sequence=2 scope=session-scope");
        Assert.NotNull(tool.LastContext);
        Assert.Equal(2, tool.LastContext!.ExecutionSequence);
        Assert.Equal("session-scope", tool.LastContext.Items!["scope"]);
    }

    [Fact]
    public async Task ExecuteAsync_HiddenToolFailsAuthorization() {
        var registry = new ToolRegistry(new ITool[] { new RecordingTool("alpha") });
        var session = new ToolSessionState(new ToolAccessPolicy(hiddenToolNames: new[] { "ALPHA" }));
        var executor = new ToolExecutor(registry, session);

        var result = await executor.ExecuteAsync(new RawToolCall("alpha", "call-1", "{}"), CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, result.ExecuteResult.Status);
        var block = Assert.Single(result.ExecuteResult.Blocks);
        Assert.Contains("不允许执行工具", Assert.IsType<ToolResultBlock.Text>(block).Content);
    }

    [Fact]
    public async Task ExecuteAsync_CanBridgeLosslesslyToToolResult() {
        var tool = new RecordingTool("alpha");
        var registry = new ToolRegistry(new ITool[] { tool });
        var executor = new ToolExecutor(registry, new ToolSessionState());
        var rawToolCall = new RawToolCall("alpha", "call-1", "{}");

        var result = await executor.ExecuteAsync(rawToolCall, CancellationToken.None);
        var bridged = result.ToToolResult();

        Assert.Same(rawToolCall, result.RawToolCall);
        Assert.Equal("alpha", bridged.ToolName);
        Assert.Equal("call-1", bridged.ToolCallId);
        Assert.Equal(ToolExecutionStatus.Success, bridged.Status);
        AssertSingleTextBlock(bridged.Blocks, "sequence=1 scope=");
    }

    [Fact]
    public void ToolExecutionContext_UsesSessionItemsAndServicesAsSingleSourceOfTruth() {
        var services = new ServiceCollectionStub();
        var items = new Dictionary<string, object?> { ["scope"] = "session-scope" };
        var session = new ToolSessionState(services: services, items: items);
        var context = new ToolExecutionContext(session, new RawToolCall("alpha", "call-1", "{}"), executionSequence: 3);

        Assert.Same(session, context.Session);
        Assert.Same(services, context.Services);
        Assert.Same(items, context.Items);
    }

    [Fact]
    public void ToolExecuteResult_Constructor_CopiesIncomingBlocks() {
        var sourceBlocks = new List<ToolResultBlock> {
            new ToolResultBlock.Text("alpha")
        };

        var result = new ToolExecuteResult(ToolExecutionStatus.Success, sourceBlocks);

        sourceBlocks.Add(new ToolResultBlock.Text("omega"));

        AssertSingleTextBlock(result.Blocks, "alpha");
    }

    [Fact]
    public void ToolExecuteResult_Constructor_RejectsNullBlockElement() {
        var blocks = new ToolResultBlock[] { null! };

        var exception = Assert.Throws<ArgumentException>(
            () => new ToolExecuteResult(ToolExecutionStatus.Success, blocks)
        );

        Assert.Contains("cannot contain null elements", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingTool : ITool {
        public RecordingTool(string name) {
            Definition = new ToolDefinition(
                name,
                $"Tool {name}.",
                new ToolSchema.Object()
            );
        }

        public ToolDefinition Definition { get; }

        public ToolExecutionContext? LastContext { get; private set; }

        public ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
            _ = cancellationToken;
            LastContext = context;

            var scope = context.Items is not null && context.Items.TryGetValue("scope", out var value)
                ? value as string
                : null;

            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    $"sequence={context.ExecutionSequence} scope={scope}"
                )
            );
        }
    }

    private static void AssertSingleTextBlock(IReadOnlyList<ToolResultBlock> blocks, string expectedText) {
        var block = Assert.Single(blocks);
        Assert.Equal(expectedText, Assert.IsType<ToolResultBlock.Text>(block).Content);
    }

    private sealed class ServiceCollectionStub : IServiceProvider {
        public object? GetService(Type serviceType) => null;
    }
}
