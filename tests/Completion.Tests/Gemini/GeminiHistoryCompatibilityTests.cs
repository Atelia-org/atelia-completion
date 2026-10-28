using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Gemini.Tests;

public sealed class GeminiHistoryCompatibilityTests {
    private static readonly CompletionDescriptor Target = new(
        "generativelanguage.googleapis.com",
        "google-gemini-generate-content-v1beta",
        "gemini-3-flash-preview");

    [Fact]
    public void PlainTextHistoryWithoutReplay_IsProjectedWithoutInventedSignature() {
        using var projected = Project([
            new ObservationMessage("Question."),
            new ActionMessage([new ActionBlock.Text("Answer.")]),
            new ObservationMessage("Continue.")
        ]);
        var contents = projected.RootElement.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());
        Assert.Equal("Answer.", contents[1].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.False(contents[1].GetProperty("parts")[0].TryGetProperty("thoughtSignature", out _));
    }

    [Fact]
    public void ImportedParallelAndSequentialCalls_PreserveIdsAndSignOnlyFirstCallOfEachAction() {
        IHistoryMessage[] history = [
            new ActionMessage([
                new ActionBlock.Text("Checking."),
                Call("first", "foreign-1", """{"q":"a"}"""),
                Call("second", "foreign-2", """{"n":2}""")]),
            Results(("second", "foreign-2"), ("first", "foreign-1")),
            new ActionMessage([Call("third", "foreign-3", "{}")]),
            Results(("third", "foreign-3"))
        ];
        using var projected = Project(history);
        var contents = projected.RootElement.GetProperty("contents");
        var parallelParts = contents[0].GetProperty("parts");
        Assert.Equal("Checking.", parallelParts[0].GetProperty("text").GetString());
        Assert.Equal("skip_thought_signature_validator", parallelParts[1].GetProperty("thoughtSignature").GetString());
        Assert.False(parallelParts[2].TryGetProperty("thoughtSignature", out _));
        Assert.Equal("foreign-1", parallelParts[1].GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("foreign-2", parallelParts[2].GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("a", parallelParts[1].GetProperty("functionCall").GetProperty("args").GetProperty("q").GetString());
        var responses = contents[1].GetProperty("parts");
        Assert.Equal("foreign-1", responses[0].GetProperty("functionResponse").GetProperty("id").GetString());
        Assert.Equal("foreign-2", responses[1].GetProperty("functionResponse").GetProperty("id").GetString());
        Assert.Equal("skip_thought_signature_validator", contents[2].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());

        using var extended = Project([.. history, new ObservationMessage("Next question.")]);
        var extendedContents = extended.RootElement.GetProperty("contents");
        for (int i = 0; i < contents.GetArrayLength(); i++) {
            Assert.Equal(contents[i].GetRawText(), extendedContents[i].GetRawText());
        }
    }

    [Theory]
    [InlineData("other.example", "google-gemini-generate-content-v1beta")]
    [InlineData("generativelanguage.googleapis.com", "other-protocol")]
    public void ForeignReplay_UsesVisibleContentWithoutLeakingNativeSignature(string provider, string api) {
        var replay = Replay(
            """{"role":"model","parts":[{"text":"private native text","thoughtSignature":"private-signature"}]}""",
            new CompletionDescriptor(provider, api, Target.Model));
        using var projected = Project([
            new ActionMessage([new ActionBlock.Text("Visible answer."), replay, Call("lookup", "foreign-id", "{}")])
        ]);
        var parts = projected.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("Visible answer.", parts[0].GetProperty("text").GetString());
        Assert.Equal("foreign-id", parts[1].GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("skip_thought_signature_validator", parts[1].GetProperty("thoughtSignature").GetString());
        Assert.DoesNotContain("private", projected.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"role":"model","parts":[{"functionCall":{"name":"lookup","id":"native-id","args":{}}}]}""")]
    [InlineData("""{"role":"model","parts":[{"functionCall":{"name":"lookup","id":"native-id","args":{}},"thoughtSignature":"actual-native-signature"}]}""")]
    public void NativeReplay_PreservesSignaturePresenceAndFunctionCall(string payload) {
        using var projected = Project([new ActionMessage([Replay(payload, Target)])]);
        using var original = JsonDocument.Parse(payload);
        var part = projected.RootElement.GetProperty("contents")[0].GetProperty("parts")[0];
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(original.RootElement.GetProperty("parts")[0].GetRawText()),
            JsonNode.Parse(part.GetRawText())));
        Assert.DoesNotContain("skip_thought_signature_validator", part.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" ", " ")]
    [InlineData("lookup", null)]
    public void NativeReplay_LeavesOptionalIdAndNameValidityToServer(string name, string? id) {
        var call = new JsonObject { ["name"] = name, ["args"] = new JsonObject() };
        if (id is not null) call["id"] = id;
        var payload = new JsonObject {
            ["role"] = "model",
            ["parts"] = new JsonArray(new JsonObject { ["functionCall"] = call })
        };
        using var projected = Project([new ActionMessage([Replay(payload.ToJsonString(), Target)])]);
        var actual = projected.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("functionCall");
        Assert.Equal(name, actual.GetProperty("name").GetString());
        Assert.Equal(id is not null, actual.TryGetProperty("id", out var actualId));
        if (id is not null) Assert.Equal(id, actualId.GetString());
    }

    [Fact]
    public void IncompleteOrInterruptedHistory_IsProjectedInSuppliedMessageOrder() {
        using var projected = Project([
            Results(("orphan", "orphan-id")),
            new ActionMessage([Call("lookup", "", "{}"), Call("lookup", "", "{}")]),
            new ObservationMessage("Interrupted."),
            Results(("different-name", ""), ("another-name", ""))
        ]);
        var contents = projected.RootElement.GetProperty("contents");
        Assert.Equal(4, contents.GetArrayLength());
        Assert.Equal("orphan-id", contents[0].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("id").GetString());
        Assert.Equal("", contents[1].GetProperty("parts")[0].GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("", contents[1].GetProperty("parts")[1].GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("Interrupted.", contents[2].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(2, contents[3].GetProperty("parts").GetArrayLength());
        Assert.Equal("different-name", contents[3].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("name").GetString());
        Assert.Equal("another-name", contents[3].GetProperty("parts")[1].GetProperty("functionResponse").GetProperty("name").GetString());
    }

    [Fact]
    public void AmbiguousResults_PreserveSuppliedOrderRatherThanPartiallyReorder() {
        using var projected = Project([
            new ActionMessage([Call("first", "one", "{}"), Call("second", "two", "{}")]),
            Results(("second", "two"), ("extra", "orphan"), ("first", "one"))
        ]);
        var parts = projected.RootElement.GetProperty("contents")[1].GetProperty("parts");
        Assert.Equal(
            new[] { "two", "orphan", "one" },
            parts.EnumerateArray().Select(p => p.GetProperty("functionResponse").GetProperty("id").GetString()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void ImportedInvalidJsonArguments_AreNotSilentlyReplacedWithEmptyObject(string arguments) {
        Assert.Throws<InvalidOperationException>(() => {
            using var projected = Project([new ActionMessage([Call("lookup", "id", arguments)])]);
        });
    }

    [Fact]
    public void ImportedMalformedJsonArguments_ReportParsingFailure() {
        Assert.ThrowsAny<JsonException>(() => {
            using var projected = Project([new ActionMessage([Call("lookup", "id", "{broken")])]);
        });
    }

    [Fact]
    public async Task PublicClient_SendsImportedHistoryAndReportsServer400WithoutRetry() {
        using var handler = new RejectingHistoryHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        var client = new GeminiClient(null, http);
        var request = Request([
            new ActionMessage([Call("lookup", "foreign-id", "{}")]),
            Results(("different-name", "foreign-id")),
            new ObservationMessage("Continue.")
        ]);

        var failure = await Assert.ThrowsAsync<CompletionFailureException>(
            () => client.StreamCompletionAsync(request, null, CancellationToken.None));

        Assert.Equal(400, failure.Failure.HttpStatusCode);
        Assert.Equal(CompletionFailureKind.Http, failure.Failure.Kind);
        Assert.Equal(1, handler.PostCount);
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var contents = body.RootElement.GetProperty("contents");
        Assert.Equal("foreign-id", contents[0].GetProperty("parts")[0].GetProperty("functionCall").GetProperty("id").GetString());
        Assert.Equal("skip_thought_signature_validator", contents[0].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("different-name", contents[1].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("name").GetString());
    }

    private static ActionBlock.ToolCall Call(string name, string id, string args)
        => new(new RawToolCall(name, id, args));

    private static ToolResultsMessage Results(params (string Name, string Id)[] calls)
        => new(null, calls.Select(c => ToolResult.FromText(c.Name, c.Id, ToolExecutionStatus.Success, "ok")).ToArray());

    private static GeminiReplayBlock Replay(string payload, CompletionDescriptor origin)
        => new(Encoding.UTF8.GetBytes(payload), origin);

    private static CompletionRequest Request(IHistoryMessage[] history)
        => new(Target.Model, new CompletionPromptPrefix("", CompletionOutputContract.ProviderDefault([]), history), []);

    private static JsonDocument Project(IHistoryMessage[] history) {
        var projected = GeminiMessageConverter.ConvertToApiRequest(Request(history), 65_536, Target);
        return JsonDocument.Parse(JsonSerializer.Serialize(projected, new JsonSerializerOptions {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }));
    }

    private sealed class RejectingHistoryHandler : HttpMessageHandler {
        public int PostCount { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.Method == HttpMethod.Get) {
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("""{"outputTokenLimit":65536}""", Encoding.UTF8, "application/json")
                };
            }
            PostCount++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent(
                    """{"error":{"code":400,"status":"INVALID_ARGUMENT","message":"fixture history rejected"}}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
