using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.ModelSpecs;
using Xunit;

namespace Atelia.Completion.Anthropic.Tests;

public sealed class AnthropicModelSpecsTests {
    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault, null)]
    [InlineData(CompletionReasoningEffort.Disabled, "low")]
    [InlineData(CompletionReasoningEffort.Low, "low")]
    [InlineData(CompletionReasoningEffort.Medium, "medium")]
    [InlineData(CompletionReasoningEffort.High, "high")]
    [InlineData(CompletionReasoningEffort.XHigh, "xhigh")]
    [InlineData(CompletionReasoningEffort.Max, "max")]
    public async Task Opus55_UsesBuiltInLimitAndMapsAllEfforts(
        CompletionReasoningEffort effort, string? expectedWireLevel
    ) {
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, reasoningEffort: effort);

        CompletionResult result = await client.StreamCompletionAsync(Request("claude-opus-5-5"), null);

        Assert.True(result.Termination.IsSuccess);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal(128_000, body.RootElement.GetProperty("max_tokens").GetInt32());
        if (expectedWireLevel is null) {
            Assert.False(body.RootElement.TryGetProperty("thinking", out _));
            Assert.False(body.RootElement.TryGetProperty("output_config", out _));
        }
        else {
            Assert.Equal("adaptive", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("summarized", body.RootElement.GetProperty("thinking").GetProperty("display").GetString());
            Assert.Equal(expectedWireLevel, body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault, false)]
    [InlineData(CompletionReasoningEffort.Disabled, false)]
    [InlineData(CompletionReasoningEffort.High, false)]
    [InlineData(CompletionReasoningEffort.ProviderDefault, true)]
    [InlineData(CompletionReasoningEffort.Disabled, true)]
    [InlineData(CompletionReasoningEffort.High, true)]
    public async Task Opus55_RejectsForcedToolChoiceBeforeDispatch(CompletionReasoningEffort effort, bool named) {
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, reasoningEffort: effort);

        CompletionRequestRejectedException failure = await Assert.ThrowsAsync<CompletionRequestRejectedException>(
            () => client.StreamCompletionAsync(Request("claude-opus-5-5", forcedToolChoice: true, named: named), null)
        );

        Assert.Equal("anthropic.incompatible-tool-choice", failure.Termination.ProviderReason);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("claude-opus-5-5", 128_000)]
    [InlineData("claude-opus-future", 90_000)]
    [InlineData("claude-future", 70_000)]
    [InlineData("other", 50_000)]
    public async Task ExactThenLongestPrefixThenGlobal_LimitSelectionSkipsGet(string modelId, int expectedLimit) {
        var catalog = BuiltinModelSpecs.AnthropicMessages
            .WithPattern("*", new() { OutputTokenLimit = 50_000 })
            .WithPattern("claude-*", new() { OutputTokenLimit = 70_000 })
            .WithPattern("claude-opus-*", new() { OutputTokenLimit = 90_000 });
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, modelSpecs: catalog);

        _ = await client.StreamCompletionAsync(Request(modelId), null);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal(modelId, body.RootElement.GetProperty("model").GetString());
        Assert.Equal(expectedLimit, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("claude-exact")]
    [InlineData("claude-opus-future")]
    public async Task SelectedWholeSpec_DoesNotBorrowLimitFromBroaderRules(string modelId) {
        var catalog = CompletionModelSpecCatalog.Empty
            .WithPattern("*", new() { OutputTokenLimit = 50_000 })
            .WithPattern("claude-*", new() { OutputTokenLimit = 70_000 })
            .WithPattern("claude-opus-*", new())
            .WithModel("claude-exact", new());
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, modelSpecs: catalog);

        _ = await client.StreamCompletionAsync(Request(modelId), null);

        Assert.Equal([HttpMethod.Get, HttpMethod.Post], handler.Requests.Select(static request => request.Method));
        using var body = JsonDocument.Parse(handler.Requests.Last().Body!);
        Assert.Equal(200_000, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task EmptyCatalog_ReplacesBuiltIns() {
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, modelSpecs: CompletionModelSpecCatalog.Empty);

        _ = await client.StreamCompletionAsync(Request("claude-opus-5-5"), null);

        Assert.Equal([HttpMethod.Get, HttpMethod.Post], handler.Requests.Select(static request => request.Method));
        using var body = JsonDocument.Parse(handler.Requests.Last().Body!);
        Assert.Equal(200_000, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task SharedSpec_DoesNotMergeActualIdsOrCapabilityCacheKeys() {
        var catalog = CompletionModelSpecCatalog.Empty.WithModels(["alias-a", "alias-b"], new());
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, modelSpecs: catalog);

        foreach (string model in new[] { "alias-a", "alias-b", "alias-a" }) {
            _ = await client.StreamCompletionAsync(Request(model), null);
        }

        Assert.Equal([HttpMethod.Get, HttpMethod.Post, HttpMethod.Get, HttpMethod.Post, HttpMethod.Post],
            handler.Requests.Select(static request => request.Method));
        Assert.Equal(["/v1/models/alias-a", "/v1/models/alias-b"],
            handler.Requests.Where(static request => request.Method == HttpMethod.Get).Select(static request => request.Uri.AbsolutePath));
        Assert.Equal(["alias-a", "alias-b", "alias-a"],
            handler.Requests.Where(static request => request.Method == HttpMethod.Post).Select(static request => ReadModel(request.Body!)));
    }

    [Theory]
    [InlineData(null, CompletionReasoningEffort.High, false)]
    [InlineData(false, CompletionReasoningEffort.ProviderDefault, false)]
    [InlineData(false, CompletionReasoningEffort.Disabled, false)]
    [InlineData(null, CompletionReasoningEffort.Disabled, true)]
    [InlineData(true, CompletionReasoningEffort.High, true)]
    public async Task ForcedToolConstraint_UsesMappedEffortBeforeGet(
        bool? supportsForcedToolChoice, CompletionReasoningEffort effort, bool shouldSucceed
    ) {
        var catalog = CompletionModelSpecCatalog.Empty.WithModel("unknown", new() {
            ForcedToolChoiceSupported = supportsForcedToolChoice
        });
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, reasoningEffort: effort, modelSpecs: catalog);

        if (shouldSucceed) {
            _ = await client.StreamCompletionAsync(Request("unknown", forcedToolChoice: true), null);
            Assert.Equal([HttpMethod.Get, HttpMethod.Post], handler.Requests.Select(static request => request.Method));
        }
        else {
            _ = await Assert.ThrowsAsync<CompletionRequestRejectedException>(
                () => client.StreamCompletionAsync(Request("unknown", forcedToolChoice: true), null)
            );
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task DisabledRoundedToEnabled_ConservativeForcedToolChoiceIsRejected() {
        var catalog = CompletionModelSpecCatalog.Empty.WithModel("unknown", new() {
            ReasoningMapper = new FixedMapper(new(CompletionReasoningEffort.Low, "low"))
        });
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, reasoningEffort: CompletionReasoningEffort.Disabled, modelSpecs: catalog);

        _ = await Assert.ThrowsAsync<CompletionRequestRejectedException>(
            () => client.StreamCompletionAsync(Request("unknown", forcedToolChoice: true), null)
        );
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ParallelPolicyWithNoTools_IsRejectedBeforeCapabilityGet() {
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient);
        var request = new CompletionRequest("unknown",
            new CompletionPromptPrefix("system",
                new CompletionOutputContract([], CompletionToolChoice.None, allowParallelToolCalls: false),
                [new ObservationMessage("hello")]), []);

        var failure = await Assert.ThrowsAsync<CompletionRequestRejectedException>(
            () => client.StreamCompletionAsync(request, null)
        );

        Assert.Equal("anthropic.invalid-parallel-tool-policy", failure.Termination.ProviderReason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NamedMapper_ProjectsCustomNameAndDefaultDoesNotInvokeMapper() {
        var mapper = new FixedMapper(new(CompletionReasoningEffort.High, "extended"));
        var catalog = CompletionModelSpecCatalog.Empty.WithModel("unknown", new() {
            OutputTokenLimit = 80_000,
            ReasoningMapper = mapper
        });
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, reasoningEffort: CompletionReasoningEffort.XHigh, modelSpecs: catalog);
        var defaultClient = new AnthropicClient(null, httpClient, modelSpecs: catalog);

        _ = await client.StreamCompletionAsync(Request("unknown"), null);
        _ = await defaultClient.StreamCompletionAsync(Request("unknown"), null);

        Assert.Equal(1, mapper.CallCount);
        using var first = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("extended", first.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        using var second = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.False(second.RootElement.TryGetProperty("thinking", out _));
        Assert.False(second.RootElement.TryGetProperty("output_config", out _));
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Low, null)]
    [InlineData(CompletionReasoningEffort.Disabled, "off")]
    public async Task UnusableMapperResult_IsConfigurationErrorBeforeGet(
        CompletionReasoningEffort effective, string? wireLevel
    ) {
        var catalog = CompletionModelSpecCatalog.Empty.WithModel("unknown", new() {
            ReasoningMapper = new FixedMapper(new(effective, wireLevel))
        });
        using var handler = new RecordingHandler();
        using var httpClient = CreateHttpClient(handler);
        var client = new AnthropicClient(null, httpClient, reasoningEffort: CompletionReasoningEffort.High, modelSpecs: catalog);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => client.StreamCompletionAsync(Request("unknown"), null));

        Assert.Empty(handler.Requests);
    }

    private static string? ReadModel(string body) {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("model").GetString();
    }

    private static CompletionRequest Request(string modelId, bool forcedToolChoice = false, bool named = false) => new(
        modelId,
        new CompletionPromptPrefix("system", forcedToolChoice
            ? new CompletionOutputContract([new ToolDefinition("emit", "Emit", new ToolSchema.Object())],
                named ? CompletionToolChoice.RequiredNamed("emit") : CompletionToolChoice.RequiredAny)
            : CompletionOutputContract.ProviderDefault([]),
            [new ObservationMessage("hello")]),
        []
    );

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler) {
        BaseAddress = new Uri("https://provider.example/")
    };

    private sealed class FixedMapper(ReasoningEffortMapping result) : ICompletionReasoningEffortMapper {
        public int CallCount { get; private set; }
        public ReasoningEffortMapping Map(CompletionReasoningEffort requested) {
            CallCount++;
            return result;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler {
        public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.Method, request.RequestUri!, request.Content is null
                ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return request.Method == HttpMethod.Get
                ? new(HttpStatusCode.OK) {
                    Content = new StringContent("{\"max_tokens\":200000}", Encoding.UTF8, "application/json")
                }
                : new(HttpStatusCode.OK) {
                    Content = new StringContent(
                        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{}}\n\n"
                        + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n"
                        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream")
                };
        }
    }
}
