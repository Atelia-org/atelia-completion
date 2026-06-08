using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tools.Tests;

public sealed class CompletionToolsQuickStartSamplesTests {
    [Fact]
    public async Task QuickStart_MethodToolWrapper_RunsThroughToolSession() {
        var host = new QuickStartTools();
        var echoTool = MethodToolWrapper.FromMethod(
            host,
            typeof(QuickStartTools).GetMethod(nameof(QuickStartTools.EchoAsync))!
        );

        var session = new ToolRegistry([echoTool]).CreateSession(
            items: new Dictionary<string, object?> { ["scope"] = "quick-start" }
        );

        var execution = await session.ExecuteAsync(
            new RawToolCall("workspace.echo", "call-1", """{"text":"hello"}"""),
            CancellationToken.None
        );

        Assert.Equal(ToolExecutionStatus.Success, execution.ExecuteResult.Status);
        Assert.Equal("hello|quick-start|1", execution.ExecuteResult.GetFlattenedText());

        var definition = Assert.Single(session.VisibleDefinitions);
        Assert.Equal("workspace.echo", definition.Name);
    }

    [Fact]
    public async Task QuickStart_ArtifactToolWrapper_CapturesStructuredArtifact() {
        var acceptedDrafts = new List<OutlineDraft>();
        var submitDraft = ArtifactToolWrapper<OutlineDraft>.Create(
            "draft.submit",
            (draft, context) => {
                acceptedDrafts.Add(draft);
                return new ValidateResult(true, $"saved:{draft.Title}|{context.ExecutionSequence}");
            }
        );

        var session = new ToolRegistry([submitDraft]).CreateSession();

        var execution = await session.ExecuteAsync(
            new RawToolCall(
                "draft.submit",
                "call-2",
                """{"title":"Atelia Tools","sections":["Why","How"]}"""
            ),
            CancellationToken.None
        );

        Assert.Equal(ToolExecutionStatus.Success, execution.ExecuteResult.Status);
        Assert.Equal("saved:Atelia Tools|1", execution.ExecuteResult.GetFlattenedText());

        var accepted = Assert.Single(acceptedDrafts);
        Assert.Equal("Atelia Tools", accepted.Title);
        Assert.Equal(new[] { "Why", "How" }, accepted.Sections);
    }

    [Fact]
    public async Task QuickStart_CompletionLoop_BridgesToolCallsToToolResultsMessage() {
        var host = new QuickStartTools();
        var echoTool = MethodToolWrapper.FromMethod(
            host,
            typeof(QuickStartTools).GetMethod(nameof(QuickStartTools.EchoAsync))!
        );

        var session = new ToolRegistry([echoTool]).CreateSession(
            items: new Dictionary<string, object?> { ["scope"] = "loop" }
        );

        var history = new List<IHistoryMessage> {
            new ObservationMessage("Call workspace.echo and return the result.")
        };

        var request = new CompletionRequest(
            ModelId: "demo-model",
            SystemPrompt: "You are a helpful assistant.",
            Context: history,
            Tools: session.VisibleDefinitions
        );

        Assert.Equal("workspace.echo", Assert.Single(request.Tools).Name);

        var assistantMessage = new ActionMessage(
            [
            new ActionBlock.ToolCall(
                new RawToolCall(
                    "workspace.echo",
                    "call-3",
                    """{"text":"hello from tool"}"""
                )
            )
        ]
        );

        history.Add(assistantMessage);

        var toolResults = new List<ToolResult>();
        foreach (var call in assistantMessage.ToolCalls) {
            var execution = await session.ExecuteAsync(call, CancellationToken.None);
            toolResults.Add(execution.ToToolResult());
        }

        var toolResultsMessage = new ToolResultsMessage(content: null, results: toolResults);
        history.Add(toolResultsMessage);

        Assert.Equal(3, history.Count);

        var toolResult = Assert.Single(toolResultsMessage.Results);
        Assert.Equal("workspace.echo", toolResult.ToolName);
        Assert.Equal("call-3", toolResult.ToolCallId);
        Assert.Equal(ToolExecutionStatus.Success, toolResult.Status);
        Assert.Equal("hello from tool|loop|1", toolResult.GetFlattenedText());
    }

    private sealed class QuickStartTools {
        [Tool("workspace.echo", "Echo text and expose session scope.")]
        public ValueTask<ToolExecuteResult> EchoAsync(
            EchoInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = cancellationToken;

            var scope = context.Items is not null && context.Items.TryGetValue("scope", out var value)
                ? value as string
                : null;

            return ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    ToolExecutionStatus.Success,
                    $"{input.Text}|{scope}|{context.ExecutionSequence}"
                )
            );
        }
    }

    [Description("Input for workspace.echo.")]
    private sealed record class EchoInput(
        [property: Description("Text to echo back.")]
        [property: JsonPropertyName("text")]
        string Text
    );

    [Description("Draft outline submitted by the model.")]
    private sealed class OutlineDraft {
        [Description("Document title.")]
        [MinLength(3)]
        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        [Description("Top-level sections.")]
        [JsonPropertyName("sections")]
        public IReadOnlyList<string> Sections { get; init; } = [];
    }
}
