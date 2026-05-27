using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesReasoningBlockTests {
    [Fact]
    public void Constructor_PreservesRawItemJsonOriginAndDebugText() {
        var origin = new CompletionDescriptor("openai", "openai-responses-v1", "gpt-5");
        const string rawItemJson = """{"id":"rs_1","type":"reasoning","encrypted_content":"abc"}""";

        var block = new OpenAIResponsesReasoningBlock(rawItemJson, origin, "Need a tool.");

        Assert.Equal(rawItemJson, block.RawItemJson);
        Assert.Equal(origin, block.Origin);
        Assert.Equal("Need a tool.", block.PlainTextForDebug);
    }
}
