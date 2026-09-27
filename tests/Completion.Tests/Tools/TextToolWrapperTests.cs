using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tools.Tests;

public sealed class TextToolWrapperTests {
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("*** Begin Patch\n+\"${text}\\\\\"\n*** End Patch\n")]
    public async Task Text_IsPassedUnchanged_WithContextAndToken(string text) {
        var host = new Host();
        var tool = TextToolWrapper.FromDelegate(host.Echo);
        using var cts = new CancellationTokenSource();
        var session = new ToolRegistry([tool]).CreateSession();
        var result = await session.ExecuteAsync(RawToolCall.FromText("echo", "call", text), cts.Token);
        Assert.Equal(ToolExecutionStatus.Success, result.ExecuteResult.Status);
        Assert.Equal(text, result.ExecuteResult.GetFlattenedText());
        Assert.Equal(cts.Token, host.Token);
        Assert.Same(session, host.Context!.Session);
        Assert.Equal(1, host.Invocations);
    }

    [Fact]
    public async Task WrongProtocolAndAccessFailure_DoNotInvokeHandler() {
        var host = new Host();
        var tool = TextToolWrapper.FromDelegate(host.Echo);
        var session = new ToolRegistry([tool]).CreateSession();
        var result = await session.ExecuteAsync(new RawToolCall("echo", "call", "{}"), default);
        Assert.Equal(ToolExecutionStatus.Failed, result.ExecuteResult.Status);
        var hidden = new ToolRegistry([tool]).CreateSession(ToolAccessSnapshot.Hide(["echo"]));
        result = await hidden.ExecuteAsync(RawToolCall.FromText("echo", "call", "text"), default);
        Assert.Equal(ToolExecutionStatus.Failed, result.ExecuteResult.Status);
        Assert.Equal(0, host.Invocations);
    }

    [Fact]
    public async Task PreCancelledToken_DoesNotInvokeHandler_AndKeepsToken() {
        var host = new Host();
        var session = new ToolRegistry([TextToolWrapper.FromDelegate(host.Echo)]).CreateSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync(RawToolCall.FromText("echo", "call", "text"), cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(0, host.Invocations);
    }

    [Fact]
    public void MethodAndDelegateEntrypoints_PreserveGrammar() {
        var host = new Host();
        var format = ToolTextFormat.Grammar("lark", "start: /.+/");
        var tool = TextToolWrapper.FromMethod(host, typeof(Host).GetMethod(nameof(Host.Echo))!, format);
        Assert.Equal(ToolInputKind.Text, tool.Definition.InputKind);
        Assert.Same(format, tool.Definition.TextFormat);
        Assert.Throws<InvalidOperationException>(() => tool.Definition.InputSchema);
        Assert.Contains("lark", ToolSchemaTextRenderer.RenderDefinition(tool.Definition));
        Assert.Contains("start:", ToolSchemaTextRenderer.RenderDefinition(tool.Definition));
    }

    [Fact]
    public void Wrapper_RejectsMissingAttributeAndMulticast() {
        Func<string, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>> anonymous =
            (text, context, ct) => ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Success, text));
        Assert.Throws<InvalidOperationException>(() => TextToolWrapper.FromDelegate(anonymous));
        var host = new Host();
        var combined = (Func<string, ToolExecutionContext, CancellationToken, ValueTask<ToolExecuteResult>>)host.Echo;
        combined += host.Echo;
        Assert.Throws<ArgumentException>(() => TextToolWrapper.FromDelegate(combined));
    }

    private sealed class Host {
        public int Invocations;
        public ToolExecutionContext? Context;
        public CancellationToken Token;
        [Tool("echo", "Echo raw text.")]
        public ValueTask<ToolExecuteResult> Echo(string input, ToolExecutionContext context, CancellationToken ct) {
            Invocations++;
            Context = context;
            Token = ct;
            return ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Success, input));
        }
    }
}
