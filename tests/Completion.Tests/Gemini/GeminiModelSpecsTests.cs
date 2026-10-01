using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.ModelSpecs;
using Xunit;

namespace Atelia.Completion.Gemini.Tests;

public sealed class GeminiModelSpecsTests {
    public static IEnumerable<object?[]> RepresentativeMappings() {
        foreach (string model in new[] {
            "gemini-3.8-flash", "gemini-3.5-flash-lite",
            "gemini-3.1-pro-preview", "gemini-3.1-flash-lite"
        }) {
            foreach ((CompletionReasoningEffort effort, string? wire) in new[] {
                (CompletionReasoningEffort.ProviderDefault, (string?)null),
                (CompletionReasoningEffort.Disabled, "low"),
                (CompletionReasoningEffort.Low, "low"),
                (CompletionReasoningEffort.Medium, "medium"),
                (CompletionReasoningEffort.High, "high"),
                (CompletionReasoningEffort.XHigh, "high"),
                (CompletionReasoningEffort.Max, "high")
            }) { yield return [model, effort, wire]; }
        }
    }

    [Theory]
    [MemberData(nameof(RepresentativeMappings))]
    public async Task Builtins_ProjectEffortAndMaximumWithoutDiscovery(
        string model, CompletionReasoningEffort effort, string? expected
    ) {
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        var client = Client(http, effort);
        CompletionResult result = await client.StreamCompletionAsync(Request(model), null);
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        Assert.Equal(model, result.Invocation.Model);
        Assert.Equal(model, Assert.Single(result.Message.Blocks.OfType<GeminiReplayBlock>()).Origin.Model);
        Assert.Null(result.Usage.OutputTokens);
        Assert.Null(result.Usage.UncachedInputTokens);
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Methods));
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        AssertConfig(body.RootElement, 65_536, expected);
    }

    [Theory]
    [InlineData("gemini-3.8-flash")]
    [InlineData("gemini-3.7-flash")]
    [InlineData("gemini-3.6-flash")]
    [InlineData("gemini-3.5-flash")]
    [InlineData("gemini-3.5-flash-lite")]
    [InlineData("gemini-3.1-flash-lite")]
    [InlineData("gemini-3.1-pro-preview")]
    public async Task BuiltinResourceNames_ShareSpecWithoutChangingInvocationIdentity(string model) {
        string resource = $"models/{model}";
        Assert.Same(BuiltinModelSpecs.GeminiGenerateContent.Lookup(model),
            BuiltinModelSpecs.GeminiGenerateContent.Lookup(resource));
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        CompletionResult result = await Client(http, CompletionReasoningEffort.High)
            .StreamCompletionAsync(Request(resource), null);
        Assert.Equal(resource, result.Invocation.Model);
        Assert.Equal(resource, Assert.Single(result.Message.Blocks.OfType<GeminiReplayBlock>()).Origin.Model);
        Assert.Equal($"/v1beta/models/{model}:streamGenerateContent", Assert.Single(handler.Paths));
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        AssertConfig(body.RootElement, 65_536, "high");
    }

    [Theory]
    [InlineData("gemini-2.5-flash")]
    [InlineData("gemini-3-flash-preview")]
    [InlineData("gemini-3.1-flash-lite-image")]
    [InlineData("gemini-3.8-flash-tts")]
    [InlineData("gemini-flash-latest")]
    [InlineData("unknown-model")]
    public async Task UnregisteredModels_DefaultDiscoversButExplicitEffortFailsBeforeHttp(string model) {
        Assert.Null(BuiltinModelSpecs.GeminiGenerateContent.Lookup(model));
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Client(http, CompletionReasoningEffort.Low).StreamCompletionAsync(Request(model), null));
        Assert.Empty(handler.Methods);
        await Client(http).StreamCompletionAsync(Request(model), null);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], handler.Methods);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        AssertConfig(body.RootElement, 77_777, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitEmptyOrUnrelatedCatalog_ReplacesAllBuiltinKnowledge(bool unrelated) {
        var specs = unrelated
            ? CompletionModelSpecCatalog.Empty.WithModel("other", new() { OutputTokenLimit = 123 })
            : CompletionModelSpecCatalog.Empty;
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Client(http, CompletionReasoningEffort.Medium, specs)
                .StreamCompletionAsync(Request("gemini-3.8-flash"), null));
        Assert.Empty(handler.Methods);
        await Client(http, specs: specs).StreamCompletionAsync(Request("gemini-3.8-flash"), null);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        AssertConfig(body.RootElement, 77_777, null);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], handler.Methods);
    }

    [Theory]
    [InlineData("private/model", 111)]
    [InlineData("private/alias", 111)]
    [InlineData("private/next-a", 222)]
    [InlineData("private/next-b", 222)]
    [InlineData("other", 333)]
    public async Task HostAliasesAndPatterns_ChooseOneSpecAndKeepEscapedRequestPath(string model, int limit) {
        var mapper = ReasoningEffortMappers.ForNamedLevels(new Dictionary<CompletionReasoningEffort, string> {
            [CompletionReasoningEffort.Low] = "minimal",
            [CompletionReasoningEffort.High] = "high"
        });
        var specs = CompletionModelSpecCatalog.Empty
            .WithPattern("*", new() { OutputTokenLimit = 333, ReasoningMapper = mapper })
            .WithPattern("private/*", new() { OutputTokenLimit = 222, ReasoningMapper = mapper })
            .WithModels(["private/model", "private/alias"], new() {
                OutputTokenLimit = 111, ReasoningMapper = mapper
            });
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        CompletionResult result = await Client(http, CompletionReasoningEffort.Disabled, specs)
            .StreamCompletionAsync(Request(model), null);
        Assert.Equal(model, result.Invocation.Model);
        Assert.Equal($"/v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent", Assert.Single(handler.Paths));
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Methods));
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        AssertConfig(body.RootElement, limit, "minimal");
    }

    [Fact]
    public async Task ExactSpecWithoutMaximum_DoesNotBorrowPatternMaximum_AndCachesByActualId() {
        var spec = new CompletionModelSpec { ReasoningMapper = ThreeLevels() };
        var specs = CompletionModelSpecCatalog.Empty
            .WithPattern("*", new() { OutputTokenLimit = 123 })
            .WithModels(["route-a", "route-b"], spec);
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        var client = Client(http, CompletionReasoningEffort.Medium, specs);
        foreach (string model in new[] { "route-a", "route-a", "route-b" }) {
            await client.StreamCompletionAsync(Request(model), null);
        }
        Assert.Equal([HttpMethod.Get, HttpMethod.Post, HttpMethod.Post, HttpMethod.Get, HttpMethod.Post], handler.Methods);
        Assert.Equal("/v1beta/models/route-a", handler.Paths[0]);
        Assert.Equal("/v1beta/models/route-b", handler.Paths[3]);
        foreach (string body in handler.Bodies) {
            using JsonDocument doc = JsonDocument.Parse(body);
            AssertConfig(doc.RootElement, 77_777, "medium");
        }
    }

    [Fact]
    public async Task ExactSpecWithoutMapper_DoesNotBorrowPatternMapperOrBuiltin() {
        var specs = BuiltinModelSpecs.GeminiGenerateContent
            .WithPattern("*", new() { ReasoningMapper = ThreeLevels() })
            .WithModel("gemini-3.8-flash", new() { OutputTokenLimit = 123 });
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Client(http, CompletionReasoningEffort.Low, specs)
                .StreamCompletionAsync(Request("gemini-3.8-flash"), null));
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public async Task ProviderDefault_DoesNotInvokeCustomMapper() {
        var mapper = new CountingMapper(new(CompletionReasoningEffort.ProviderDefault, "invalid"));
        var specs = CompletionModelSpecCatalog.Empty.WithModel("route", new() {
            OutputTokenLimit = 123, ReasoningMapper = mapper
        });
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        await Client(http, specs: specs).StreamCompletionAsync(Request("route"), null);
        Assert.Equal(0, mapper.Calls);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        AssertConfig(body.RootElement, 123, null);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Low, "minimal")]
    [InlineData(CompletionReasoningEffort.Medium, "LOW")]
    public async Task CustomMapper_IsConsumedOnceWithoutApplyingAnotherRoundingPass(
        CompletionReasoningEffort effective, string wire
    ) {
        var mapper = new CountingMapper(new(effective, wire));
        var specs = CompletionModelSpecCatalog.Empty.WithModel("route", new() {
            OutputTokenLimit = 123, ReasoningMapper = mapper
        });
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        await Client(http, CompletionReasoningEffort.Max, specs).StreamCompletionAsync(Request("route"), null);
        Assert.Equal(1, mapper.Calls);
        Assert.Equal(CompletionReasoningEffort.Max, mapper.Requested);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        AssertConfig(body.RootElement, 123, wire);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault, "low")]
    [InlineData((CompletionReasoningEffort)99, "low")]
    [InlineData(CompletionReasoningEffort.Disabled, "minimal")]
    [InlineData(CompletionReasoningEffort.Disabled, null)]
    [InlineData(CompletionReasoningEffort.Low, null)]
    [InlineData(CompletionReasoningEffort.Low, " ")]
    [InlineData(CompletionReasoningEffort.Low, "extended")]
    [InlineData(CompletionReasoningEffort.Low, "1024")]
    [InlineData(CompletionReasoningEffort.Low, "Low")]
    public async Task InvalidMapperResults_FailBeforeCapabilityHttp(CompletionReasoningEffort effective, string? wire) {
        var specs = CompletionModelSpecCatalog.Empty.WithModel("route", new() {
            ReasoningMapper = new CountingMapper(new(effective, wire))
        });
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Client(http, CompletionReasoningEffort.Low, specs).StreamCompletionAsync(Request("route"), null));
        Assert.Empty(handler.Methods);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault, false)]
    [InlineData(CompletionReasoningEffort.Disabled, true)]
    [InlineData(CompletionReasoningEffort.High, false)]
    public async Task ForcedFalse_RejectsBeforeDiscoveryRegardlessOfEffort(CompletionReasoningEffort effort, bool named) {
        var specs = CompletionModelSpecCatalog.Empty.WithModel("route", new() {
            ReasoningMapper = ThreeLevels(), ForcedToolChoiceSupported = false
        });
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        var contract = new CompletionOutputContract([Tool()], named
            ? CompletionToolChoice.RequiredNamed("emit") : CompletionToolChoice.RequiredAny);
        var error = await Assert.ThrowsAsync<CompletionRequestRejectedException>(() =>
            Client(http, effort, specs).StreamCompletionAsync(Request("route", contract), null));
        Assert.Equal("model_specs.incompatible_tool_choice", error.Termination.ProviderReason);
        Assert.Empty(handler.Methods);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, true)]
    public async Task ForcedNullOrTrue_PreservesAnyAndNamedWithThinking(bool? supported, bool named) {
        var specs = CompletionModelSpecCatalog.Empty.WithModel("route", new() {
            OutputTokenLimit = 123, ReasoningMapper = ThreeLevels(), ForcedToolChoiceSupported = supported
        });
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        var contract = new CompletionOutputContract([Tool()], named
            ? CompletionToolChoice.RequiredNamed("emit") : CompletionToolChoice.RequiredAny);
        await Client(http, CompletionReasoningEffort.Disabled, specs)
            .StreamCompletionAsync(Request("route", contract), null);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        AssertConfig(body.RootElement, 123, "low");
        JsonElement config = body.RootElement.GetProperty("toolConfig").GetProperty("functionCallingConfig");
        Assert.Equal("ANY", config.GetProperty("mode").GetString());
        Assert.Equal(named, config.TryGetProperty("allowedFunctionNames", out JsonElement names));
        if (named) { Assert.Equal("emit", Assert.Single(names.EnumerateArray()).GetString()); }
    }

    [Fact]
    public async Task ParallelPolicyAndInvalidReplay_FailBeforeDiscovery() {
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        var client = Client(http);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.StreamCompletionAsync(
            Request("unknown", new CompletionOutputContract([Tool()], CompletionToolChoice.Auto,
                allowParallelToolCalls: false)), null));
        var request = new CompletionRequest("unknown", new CompletionPromptPrefix("",
            CompletionOutputContract.ProviderDefault([]), [new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("emit", "call-1", "{}"))
            ])]), []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StreamCompletionAsync(request, null));
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public async Task ChangingEffortAndTargetModel_PreservesToolSignatureAndFunctionResponseIdentity() {
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        var origin = new CompletionDescriptor("example.invalid", "google-gemini-generate-content-v1beta",
            "gemini-3.1-flash-lite");
        var replay = new GeminiReplayBlock(Encoding.UTF8.GetBytes("""
            {"role":"model","parts":[{"thoughtSignature":"signed-tool","functionCall":{"name":"emit","id":"call-1","args":{}}}]}
            """), origin);
        var request = new CompletionRequest("gemini-3.8-flash", new CompletionPromptPrefix("",
            new CompletionOutputContract([Tool()], CompletionToolChoice.RequiredNamed("emit")),
            [new ObservationMessage("Emit."), new ActionMessage([replay]),
             new ToolResultsMessage(null, [ToolResult.FromText("emit", "call-1", ToolExecutionStatus.Success, "ok")])]), []);
        await Client(http, CompletionReasoningEffort.Disabled).StreamCompletionAsync(request, null);
        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        AssertConfig(body.RootElement, 65_536, "low");
        JsonElement contents = body.RootElement.GetProperty("contents");
        Assert.Equal("signed-tool", contents[1].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
        JsonElement response = contents[2].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("call-1", response.GetProperty("id").GetString());
        Assert.Equal("emit", response.GetProperty("name").GetString());
        Assert.Same(origin, replay.Origin);
    }

    [Fact]
    public void Constructor_RejectsUndefinedEffort() {
        using var handler = new RecordingHandler();
        using var http = CreateHttp(handler);
        Assert.Throws<ArgumentOutOfRangeException>(() => Client(http, (CompletionReasoningEffort)99));
    }

    private static ICompletionReasoningEffortMapper ThreeLevels() => ReasoningEffortMappers.ForSupportedLevels([
        CompletionReasoningEffort.Low, CompletionReasoningEffort.Medium, CompletionReasoningEffort.High
    ]);

    private static GeminiClient Client(HttpClient http,
        CompletionReasoningEffort effort = CompletionReasoningEffort.ProviderDefault,
        CompletionModelSpecCatalog? specs = null) => new(null, http, new GeminiClientOptions {
            ReasoningEffort = effort, ModelSpecs = specs
        });

    private static ToolDefinition Tool() => new("emit", "Emit a result.", new ToolSchema.Object());

    private static CompletionRequest Request(string model, CompletionOutputContract? contract = null) => new(model,
        new CompletionPromptPrefix("", contract ?? CompletionOutputContract.ProviderDefault([]),
            [new ObservationMessage("Hello.")]), []);

    private static HttpClient CreateHttp(HttpMessageHandler handler) => new(handler) {
        BaseAddress = new Uri("https://example.invalid/"), Timeout = Timeout.InfiniteTimeSpan
    };

    private static void AssertConfig(JsonElement root, int maximum, string? level) {
        JsonElement config = root.GetProperty("generationConfig");
        Assert.Equal(maximum, config.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(level is not null, config.TryGetProperty("thinkingConfig", out JsonElement thinking));
        if (level is not null) {
            Assert.Equal(level, thinking.GetProperty("thinkingLevel").GetString());
            Assert.False(thinking.TryGetProperty("includeThoughts", out _));
            Assert.False(thinking.TryGetProperty("thinkingBudget", out _));
        }
    }

    private sealed class CountingMapper(ReasoningEffortMapping result) : ICompletionReasoningEffortMapper {
        public int Calls { get; private set; }
        public CompletionReasoningEffort Requested { get; private set; }
        public ReasoningEffortMapping Map(CompletionReasoningEffort requested) {
            Calls++;
            Requested = requested;
            return result;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler {
        public List<HttpMethod> Methods { get; } = [];
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            Methods.Add(request.Method);
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get) {
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"outputTokenLimit\":77777}", Encoding.UTF8, "application/json") };
            }
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\",\"thoughtSignature\":\"sig\"}]},\"finishReason\":\"STOP\"}]}\n\n",
                Encoding.UTF8, "text/event-stream") };
        }
    }
}
