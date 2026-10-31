using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Gemini.Tests;

public sealed class GeminiReasoningTextTests {
    private static readonly CompletionDescriptor Invocation = new(
        "example.invalid", "google-gemini-generate-content-v1beta", "gemini-3.8-flash");

    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault, true, null)]
    [InlineData(CompletionReasoningEffort.ProviderDefault, false, null)]
    [InlineData(CompletionReasoningEffort.Low, true, "low")]
    [InlineData(CompletionReasoningEffort.Low, false, "low")]
    [InlineData(CompletionReasoningEffort.Disabled, true, "low")]
    [InlineData(CompletionReasoningEffort.Disabled, false, "low")]
    public async Task RequestReasoningText_ProjectsIndependentlyOfEffortAndKeepsReceivedSummary(
        CompletionReasoningEffort effort, bool requested, string? level
    ) {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        var client = new GeminiClient(null, http, new GeminiClientOptions { ReasoningEffort = effort });
        var deltas = new List<string>();
        var observer = new CompletionStreamObserver();
        observer.ReceivedReasoningDelta += deltas.Add;

        CompletionResult result = await client.StreamCompletionAsync(Request(),
            new CompletionInvocationOptions { RequestReasoningText = requested }, observer);

        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        JsonElement config = body.RootElement.GetProperty("generationConfig");
        Assert.Equal(requested || level is not null, config.TryGetProperty("thinkingConfig", out var thinking));
        if (requested || level is not null) {
            Assert.Equal(requested, thinking.TryGetProperty("includeThoughts", out var include));
            if (requested) { Assert.True(include.GetBoolean()); }
            Assert.Equal(level is not null, thinking.TryGetProperty("thinkingLevel", out var actualLevel));
            if (level is not null) { Assert.Equal(level, actualLevel.GetString()); }
            Assert.False(thinking.TryGetProperty("thinkingBudget", out _));
        }
        Assert.Equal("answer", result.Message.GetFlattenedText());
        Assert.Equal("summary", Assert.Single(result.Message.Blocks.OfType<GeminiReplayBlock>()).PlainText);
        Assert.Equal(["summary"], deltas);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothInvocationOverloads_RequestSummaryByDefault(bool explicitOptions) {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        var client = new GeminiClient(null, http);
        CompletionResult result = explicitOptions
            ? await client.StreamCompletionAsync(Request(), new CompletionInvocationOptions(), null)
            : await client.StreamCompletionAsync(Request(), null);

        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        JsonElement thinking = body.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.True(thinking.GetProperty("includeThoughts").GetBoolean());
        Assert.False(thinking.TryGetProperty("thinkingLevel", out _));
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
    }

    [Fact]
    public void SummaryText_HasSeparateDeltasAndLifecycleWhileKeepingOneCompleteReplayBlock() {
        var events = new List<string>();
        var observer = new CompletionStreamObserver();
        observer.ReceivedThinkingBegin += () => events.Add("begin");
        observer.ReceivedThinkingEnd += () => events.Add("end");
        observer.ReceivedReasoningDelta += delta => events.Add("reasoning:" + delta);
        observer.ReceivedTextDelta += delta => events.Add("text:" + delta);
        observer.ReceivedToolCall += call => events.Add("tool:" + call.ToolName);
        var parser = new GeminiStreamParser();
        var aggregator = new CompletionAggregator(Invocation, observer);

        parser.ParseEvent(Envelope("""{"text":"first", "thought":true} """), aggregator);
        parser.ParseEvent(Envelope("""{"text":" second", "thought":true} """), aggregator);
        parser.ParseEvent(Envelope("""{"text":"answer", "thought":false} """), aggregator);
        parser.ParseEvent(Envelope("""{"text":"late", "thought":true, "thoughtSignature":"summary-sig"} """), aggregator);
        parser.ParseEvent(Envelope("""{"functionCall":{"name":"lookup", "id":"call-1", "args":{}}, "thoughtSignature":"call-sig"} """), aggregator);
        parser.ParseEvent("""{"candidates":[{"finishReason":"STOP"}]}""", aggregator);

        CompletionResult result = aggregator.Build();
        Assert.Equal("answer", result.Message.GetFlattenedText());
        var replay = Assert.Single(result.Message.Blocks.OfType<GeminiReplayBlock>());
        Assert.Equal("first secondlate", replay.PlainText);
        Assert.Equal(["begin", "reasoning:first", "reasoning: second", "end", "text:answer",
            "begin", "reasoning:late", "end", "tool:lookup"], events);
        using JsonDocument payload = JsonDocument.Parse(replay.OpaquePayload);
        JsonElement parts = payload.RootElement.GetProperty("parts");
        Assert.Equal(5, parts.GetArrayLength());
        Assert.True(parts[0].GetProperty("thought").GetBoolean());
        Assert.False(parts[2].GetProperty("thought").GetBoolean());
        Assert.False(parts[4].TryGetProperty("thought", out _));
        Assert.Equal("summary-sig", parts[3].GetProperty("thoughtSignature").GetString());
        Assert.Equal("call-sig", parts[4].GetProperty("thoughtSignature").GetString());
    }

    [Theory]
    [InlineData("{\"candidates\":[{\"finishReason\":\"MAX_TOKENS\"}]}", CompletionTerminationKind.Incomplete)]
    [InlineData("{\"error\":{\"message\":\"failure\",\"status\":\"FAILED\"}}", CompletionTerminationKind.Failed)]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}", CompletionTerminationKind.Incomplete)]
    public void AllTerminalKinds_CloseThinkingAndKeepReadableSummary(string terminal, CompletionTerminationKind kind) {
        int begins = 0, ends = 0;
        var observer = new CompletionStreamObserver();
        observer.ReceivedThinkingBegin += () => begins++;
        observer.ReceivedThinkingEnd += () => ends++;
        var aggregator = new CompletionAggregator(Invocation, observer);
        var parser = new GeminiStreamParser();
        parser.ParseEvent(Envelope("""{"text":"summary", "thought":true} """), aggregator);
        parser.ParseEvent(terminal, aggregator);

        CompletionResult result = aggregator.Build();
        Assert.Equal(kind, result.Termination.Kind);
        Assert.Equal(1, begins);
        Assert.Equal(1, ends);
        Assert.Equal("summary", Assert.Single(result.Message.Blocks.OfType<GeminiReplayBlock>()).PlainText);
        Assert.Equal("", result.Message.GetFlattenedText());
    }

    [Theory]
    [InlineData("{\"thought\":true,\"thoughtSignature\":\"sig\"}")]
    [InlineData("{\"thought\":true,\"text\":\"\",\"thoughtSignature\":\"sig\"}")]
    public void ThoughtWithoutReadableText_ReportsUnknownPlainTextAndPreservesPart(string part) {
        var aggregator = new CompletionAggregator(Invocation);
        var parser = new GeminiStreamParser();
        parser.ParseEvent(Envelope(part, terminal: true), aggregator);
        var replay = Assert.Single(aggregator.Build().Message.Blocks.OfType<GeminiReplayBlock>());
        Assert.Null(replay.PlainText);
        using JsonDocument payload = JsonDocument.Parse(replay.OpaquePayload);
        using JsonDocument original = JsonDocument.Parse(part);
        Assert.True(JsonElement.DeepEquals(original.RootElement, payload.RootElement.GetProperty("parts")[0]));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void MalformedThoughtMarker_IsRejectedWithoutExposingItsText(string thought) {
        var observer = new CompletionStreamObserver();
        observer.ReceivedTextDelta += _ => Assert.Fail("Malformed thought was exposed as body text.");
        observer.ReceivedReasoningDelta += _ => Assert.Fail("Malformed thought was exposed as reasoning text.");
        var parser = new GeminiStreamParser();
        var aggregator = new CompletionAggregator(Invocation, observer);
        Assert.Throws<InvalidDataException>(() => parser.ParseEvent(
            Envelope($$"""{"text":"private", "thought":{{thought}}} """), aggregator));
        Assert.False(parser.TerminalEventObserved);
    }

    [Fact]
    public void SummaryReplay_RoundTripsCodecAndNativePartsWithoutEnteringVisibleText() {
        var aggregator = new CompletionAggregator(Invocation);
        var parser = new GeminiStreamParser();
        parser.ParseEvent(Envelope("""{"thought":true,"text":"summary", "thoughtSignature":"reason-sig"} """), aggregator);
        parser.ParseEvent(Envelope("""{"thought":false,"text":"answer", "thoughtSignature":"text-sig"} """, terminal: true), aggregator);
        ActionMessage message = aggregator.Build().Message;
        var registry = ReasoningBlockCodecRegistry.CreateDefault();
        ReasoningBlockCodecs.RegisterAll(registry);
        ActionMessage restored = ActionMessageSerialization.Deserialize(
            ActionMessageSerialization.Serialize(message, registry), registry);
        var replay = Assert.Single(restored.Blocks.OfType<GeminiReplayBlock>());
        Assert.Equal("summary", replay.PlainText);

        var request = new CompletionRequest(Invocation.Model, new CompletionPromptPrefix("",
            CompletionOutputContract.ProviderDefault([]), [restored, new ObservationMessage("Continue.")]), []);
        var projected = GeminiMessageConverter.ProjectRequest(request, Invocation);
        JsonElement root = JsonSerializer.SerializeToElement(projected,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        JsonElement sentParts = root.GetProperty("contents")[0].GetProperty("parts");
        using JsonDocument payload = JsonDocument.Parse(replay.OpaquePayload);
        Assert.True(JsonElement.DeepEquals(payload.RootElement.GetProperty("parts"), sentParts));
        Assert.Equal("answer", restored.GetFlattenedText());

        var contradicted = new ActionMessage([new ActionBlock.Text("changed"), replay]);
        var invalidRequest = new CompletionRequest(Invocation.Model, new CompletionPromptPrefix("",
            CompletionOutputContract.ProviderDefault([]), [contradicted]), []);
        Assert.Throws<InvalidOperationException>(() => GeminiMessageConverter.ProjectRequest(invalidRequest, Invocation));
    }

    private static string Envelope(string part, bool terminal = false) =>
        "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + part + "]}"
        + (terminal ? ",\"finishReason\":\"STOP\"" : "") + "}]}";

    private static CompletionRequest Request() => new(Invocation.Model, new CompletionPromptPrefix("",
        CompletionOutputContract.ProviderDefault([]), [new ObservationMessage("Question.")]), []);

    private sealed class RecordingHandler : HttpMessageHandler {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Assert.Equal(HttpMethod.Post, request.Method);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("data: " + Envelope("""{"thought":true,"text":"summary"} """) + "\n\n"
                    + "data: " + Envelope("""{"text":"answer"} """, terminal: true) + "\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
