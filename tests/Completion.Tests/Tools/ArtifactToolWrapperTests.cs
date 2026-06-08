using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tools.Tests;

public sealed class ArtifactToolWrapperTests {
    [Fact]
    public async Task ExecuteAsync_PassesToolExecutionContextToArtifactHandler() {
        ToolExecutionContext? observedContext = null;
        ArtifactEnvelope? observedArtifact = null;

        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "artifact.with_context",
            (artifact, context) => {
                observedArtifact = artifact;
                observedContext = context;

                var scope = context.Items is not null && context.Items.TryGetValue("scope", out var value)
                    ? value as string
                    : null;

                return new ValidateResult(true, $"{artifact.Title}|{scope}|{context.ExecutionSequence}");
            }
        );

        var session = new ToolRegistry(Array.Empty<ITool>()).CreateSession(items: new Dictionary<string, object?> { ["scope"] = "artifact-scope" });
        var context = new ToolExecutionContext(
            session,
            new RawToolCall("artifact.with_context", "call-2", """{"Title":"draft"}"""),
            executionSequence: 11
        );

        var result = await wrapper.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        AssertSingleTextBlock(result.Blocks, "draft|artifact-scope|11");
        Assert.Equal("draft|artifact-scope|11", result.GetFlattenedText());
        Assert.Same(context, observedContext);
        Assert.NotNull(observedArtifact);
        Assert.Equal("draft", observedArtifact!.Title);
    }

    [Fact]
    public async Task ExecuteAsync_WhenObjectGraphValidationFails_DoesNotInvokeArtifactHandler() {
        var handlerCalled = false;
        var wrapper = ArtifactToolWrapper<ArtifactEnvelope>.Create(
            "artifact.validation_failure",
            (artifact, context) => {
                _ = artifact;
                _ = context;
                handlerCalled = true;
                return new ValidateResult(true, "should not happen");
            }
        );

        var context = new ToolExecutionContext(
            new ToolRegistry(Array.Empty<ITool>()).CreateSession(),
            new RawToolCall("artifact.validation_failure", "call-3", """{"Title":"a"}"""),
            executionSequence: 12
        );

        var result = await wrapper.ExecuteAsync(context, CancellationToken.None);

        Assert.False(handlerCalled);
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        var content = result.GetFlattenedText();
        AssertSingleTextBlock(result.Blocks, content);
        Assert.Contains("工具参数验证失败", content, StringComparison.Ordinal);
        Assert.Contains("Title", content, StringComparison.Ordinal);
        Assert.Contains("raw_arguments_json", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDeserializationFails_DoesNotInvokeArtifactHandler() {
        var handlerCalled = false;
        var wrapper = ArtifactToolWrapper<UndeserializableArtifactEnvelope>.Create(
            "artifact.deserialize_failure",
            (artifact, context) => {
                _ = artifact;
                _ = context;
                handlerCalled = true;
                return new ValidateResult(true, "should not happen");
            }
        );

        var context = new ToolExecutionContext(
            new ToolRegistry(Array.Empty<ITool>()).CreateSession(),
            new RawToolCall("artifact.deserialize_failure", "call-4", """{"Title":"draft"}"""),
            executionSequence: 13
        );

        var result = await wrapper.ExecuteAsync(context, CancellationToken.None);

        Assert.False(handlerCalled);
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        var content = result.GetFlattenedText();
        AssertSingleTextBlock(result.Blocks, content);
        Assert.Contains("工具参数反序列化失败", content, StringComparison.Ordinal);
        Assert.Contains("raw_arguments_json", content, StringComparison.Ordinal);
    }

    [Description("Artifact envelope.")]
    private sealed class ArtifactEnvelope {
        [Description("Artifact title.")]
        [MinLength(2)]
        public string Title { get; init; } = string.Empty;
    }

    [Description("Artifact envelope that cannot be deserialized by System.Text.Json.")]
    private sealed class UndeserializableArtifactEnvelope {
        private UndeserializableArtifactEnvelope(string title) {
            Title = title;
        }

        [Description("Artifact title.")]
        public string Title { get; } = string.Empty;
    }

    private static void AssertSingleTextBlock(IReadOnlyList<ToolResultBlock> blocks, string expectedText) {
        var block = Assert.Single(blocks);
        Assert.Equal(expectedText, Assert.IsType<ToolResultBlock.Text>(block).Content);
    }
}
