using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tools.Tests;

public sealed class ToolSessionTests {
    [Fact]
    public void VisibleDefinitions_UsesSessionAccess() {
        var alpha = new RecordingTool("alpha");
        var beta = new RecordingTool("beta");
        var registry = new ToolRegistry(new ITool[] { alpha, beta });
        var session = registry.CreateSession(ToolAccessSnapshot.Hide(["BETA"]));

        var visibleDefinitions = session.VisibleDefinitions;

        var visibleDefinition = Assert.Single(visibleDefinitions);
        Assert.Equal("alpha", visibleDefinition.Name);
        Assert.True(session.TryGetTool("ALPHA", out var visibleTool));
        Assert.Same(alpha, visibleTool);
        Assert.False(session.TryGetTool("beta", out _));
    }

    [Fact]
    public async Task ExecuteAsync_UsesSessionSequenceAndContextItems() {
        var tool = new RecordingTool("alpha");
        var registry = new ToolRegistry(new ITool[] { tool });
        var session = registry.CreateSession(items: new Dictionary<string, object?> { ["scope"] = "session-scope" });

        var first = await session.ExecuteAsync(new RawToolCall("alpha", "call-1", "{}"), CancellationToken.None);
        var second = await session.ExecuteAsync(new RawToolCall("alpha", "call-2", "{}"), CancellationToken.None);

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
    public async Task ExecuteAsync_SequenceSurvivesAccessSwapAndRegistryRebind() {
        // 方案 F 的核心承诺：执行序号是 session 级的，
        // 既不随逐轮替换 Access 重置，也不随工具集变化 rebind Registry 重置。
        var registry = new ToolRegistry(new ITool[] { new RecordingTool("alpha") });
        var session = registry.CreateSession();

        var first = await session.ExecuteAsync(new RawToolCall("alpha", "call-1", "{}"), CancellationToken.None);
        AssertSingleTextBlock(first.ExecuteResult.Blocks, "sequence=1 scope=");

        session.Access = ToolAccessSnapshot.Hide(["unused"]);
        var second = await session.ExecuteAsync(new RawToolCall("alpha", "call-2", "{}"), CancellationToken.None);
        AssertSingleTextBlock(second.ExecuteResult.Blocks, "sequence=2 scope=");

        session.Registry = new ToolRegistry(new ITool[] { new RecordingTool("alpha"), new RecordingTool("beta") });
        var third = await session.ExecuteAsync(new RawToolCall("beta", "call-3", "{}"), CancellationToken.None);
        AssertSingleTextBlock(third.ExecuteResult.Blocks, "sequence=3 scope=");
        Assert.Equal(2, session.VisibleDefinitions.Length);
    }

    [Fact]
    public async Task ExecuteAsync_HiddenToolFailsAuthorization() {
        var registry = new ToolRegistry(new ITool[] { new RecordingTool("alpha") });
        var session = registry.CreateSession(ToolAccessSnapshot.Hide(["ALPHA"]));

        var result = await session.ExecuteAsync(new RawToolCall("alpha", "call-1", "{}"), CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, result.ExecuteResult.Status);
        var block = Assert.Single(result.ExecuteResult.Blocks);
        Assert.Contains("不允许执行工具", Assert.IsType<ToolResultBlock.Text>(block).Content);
    }

    [Fact]
    public void VisibleDefinitions_AllowOnlyModeRestrictsVisibilityAndExecution() {
        var alpha = new RecordingTool("alpha");
        var beta = new RecordingTool("beta");
        var registry = new ToolRegistry(new ITool[] { alpha, beta });
        var session = registry.CreateSession(ToolAccessSnapshot.AllowOnly(["beta"]));

        var visibleDefinition = Assert.Single(session.VisibleDefinitions);
        Assert.Equal("beta", visibleDefinition.Name);
        Assert.False(session.TryGetTool("alpha", out _));
        Assert.True(session.TryGetTool("beta", out var visibleTool));
        Assert.Same(beta, visibleTool);
    }

    [Fact]
    public void Intersect_AllowOnlyAndHide_ProducesExpectedCapabilityIntersection() {
        var combined = ToolAccessSnapshot.Intersect(
            ToolAccessSnapshot.AllowOnly(["alpha", "beta"]),
            ToolAccessSnapshot.Hide(["beta", "gamma"])
        );

        Assert.Equal(ToolAccessMode.AllowOnlyListed, combined.Mode);
        Assert.True(combined.IsVisible("alpha"));
        Assert.False(combined.IsVisible("beta"));
        Assert.False(combined.IsVisible("gamma"));
    }

    [Fact]
    public async Task ExecuteAsync_CanBridgeLosslesslyToToolResult() {
        var tool = new RecordingTool("alpha");
        var registry = new ToolRegistry(new ITool[] { tool });
        var session = registry.CreateSession();
        var rawToolCall = new RawToolCall("alpha", "call-1", "{}");

        var result = await session.ExecuteAsync(rawToolCall, CancellationToken.None);
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
        var session = new ToolRegistry(Array.Empty<ITool>()).CreateSession(services: services, items: items);
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
