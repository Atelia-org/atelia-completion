using System.ComponentModel;
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

        var session = new ToolSessionState(items: new Dictionary<string, object?> { ["scope"] = "artifact-scope" });
        var context = new ToolExecutionContext(
            session,
            new RawToolCall("artifact.with_context", "call-2", """{"Title":"draft"}"""),
            executionSequence: 11
        );

        var result = await wrapper.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.Equal("draft|artifact-scope|11", result.Content);
        Assert.Same(context, observedContext);
        Assert.NotNull(observedArtifact);
        Assert.Equal("draft", observedArtifact!.Title);
    }

    [Description("Artifact envelope.")]
    private sealed class ArtifactEnvelope {
        [Description("Artifact title.")]
        public string Title { get; init; } = string.Empty;
    }
}
