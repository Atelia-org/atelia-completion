using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.ModelSpecs;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIChatModelSpecsTests {
    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault, null)]
    [InlineData(CompletionReasoningEffort.Disabled, "low")]
    [InlineData(CompletionReasoningEffort.Low, "low")]
    [InlineData(CompletionReasoningEffort.Medium, "high")]
    [InlineData(CompletionReasoningEffort.High, "high")]
    [InlineData(CompletionReasoningEffort.XHigh, "high")]
    [InlineData(CompletionReasoningEffort.Max, "max")]
    public async Task KnownGlm_UsesSupportedLevelsAndPreservesModelId(
        CompletionReasoningEffort effort,
        string? expected
    ) {
        using JsonDocument document = await CaptureRequestAsync("glm-5.3", effort);
        Assert.Equal("glm-5.3", document.RootElement.GetProperty("model").GetString());
        AssertEffort(document.RootElement, expected);
        Assert.False(document.RootElement.TryGetProperty("thinking", out _));
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault, null, null)]
    [InlineData(CompletionReasoningEffort.Disabled, null, "disabled")]
    [InlineData(CompletionReasoningEffort.Low, "low", "enabled")]
    [InlineData(CompletionReasoningEffort.Medium, "high", "enabled")]
    [InlineData(CompletionReasoningEffort.High, "high", "enabled")]
    [InlineData(CompletionReasoningEffort.XHigh, "high", "enabled")]
    [InlineData(CompletionReasoningEffort.Max, "max", "enabled")]
    public async Task KnownDeepSeek_UsesBothEffortAndThinkingFields(
        CompletionReasoningEffort effort,
        string? expectedEffort,
        string? expectedThinking
    ) {
        using JsonDocument document = await CaptureRequestAsync(
            "deepseek-flash", effort, dialect: OpenAIChatDialects.DeepSeekV4
        );
        Assert.Equal("deepseek-flash", document.RootElement.GetProperty("model").GetString());
        AssertEffort(document.RootElement, expectedEffort);
        AssertThinking(document.RootElement, expectedThinking);
    }

    [Theory]
    [InlineData("glm-5.3", CompletionReasoningEffort.Disabled, "none")]
    [InlineData("glm-5.3", CompletionReasoningEffort.Medium, "medium")]
    [InlineData("glm-5.3", CompletionReasoningEffort.XHigh, "xhigh")]
    [InlineData("unrecognized-model", CompletionReasoningEffort.XHigh, "xhigh")]
    public async Task EmptyOrUnmatchedCatalog_PreservesStandardProtocolProjection(
        string modelId,
        CompletionReasoningEffort effort,
        string expected
    ) {
        using JsonDocument document = await CaptureRequestAsync(
            modelId, effort, modelSpecs: CompletionModelSpecCatalog.Empty
        );
        AssertEffort(document.RootElement, expected);
    }

    [Fact]
    public async Task UnmatchedDefaultCatalog_PreservesStandardProtocolProjection() {
        using JsonDocument document = await CaptureRequestAsync(
            "unknown-model", CompletionReasoningEffort.XHigh
        );
        AssertEffort(document.RootElement, "xhigh");
    }

    [Fact]
    public async Task EmptyDeepSeekCatalog_PreservesExistingDialectBehavior() {
        using JsonDocument document = await CaptureRequestAsync(
            "deepseek-flash", CompletionReasoningEffort.Low,
            CompletionModelSpecCatalog.Empty, OpenAIChatDialects.DeepSeekV4
        );
        AssertEffort(document.RootElement, "high");
        AssertThinking(document.RootElement, "enabled");
    }

    [Fact]
    public async Task ExplicitCatalog_ReplacesEntireDefaultCatalog() {
        var specs = CompletionModelSpecCatalog.Empty.WithModel(
            "private-model", new CompletionModelSpec()
        );
        using JsonDocument document = await CaptureRequestAsync(
            "glm-5.3", CompletionReasoningEffort.Disabled, specs
        );
        AssertEffort(document.RootElement, "none");
    }

    [Fact]
    public async Task ProviderDefault_DoesNotInvokeCustomMapper() {
        var mapper = new DelegateMapper(_ => throw new InvalidOperationException("Mapper must not run."));
        var specs = WithMapper("private-model", mapper);
        using JsonDocument document = await CaptureRequestAsync(
            "private-model", CompletionReasoningEffort.ProviderDefault, specs
        );
        AssertEffort(document.RootElement, null);
        AssertThinking(document.RootElement, null);
        Assert.Equal(0, mapper.Calls);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, CompletionReasoningEffort.Disabled, "off")]
    [InlineData(CompletionReasoningEffort.Medium, CompletionReasoningEffort.High, "extended")]
    [InlineData(CompletionReasoningEffort.XHigh, CompletionReasoningEffort.Max, "custom-max")]
    public async Task CustomMapper_ControlsPolicyAndNames(
        CompletionReasoningEffort requested,
        CompletionReasoningEffort mapped,
        string wire
    ) {
        var mapper = new DelegateMapper(effort => {
            Assert.Equal(requested, effort);
            return new ReasoningEffortMapping(mapped, wire);
        });
        using JsonDocument document = await CaptureRequestAsync(
            "private-model", requested, WithMapper("private-model", mapper)
        );
        AssertEffort(document.RootElement, wire);
        Assert.Equal(1, mapper.Calls);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, "off")]
    [InlineData(CompletionReasoningEffort.Low, "standard")]
    [InlineData(CompletionReasoningEffort.Medium, "extended")]
    [InlineData(CompletionReasoningEffort.XHigh, "extended")]
    [InlineData(CompletionReasoningEffort.Max, "extended")]
    public async Task NamedLevels_UseSharedRoundingAndCustomCloseName(
        CompletionReasoningEffort effort,
        string expected
    ) {
        var mapper = ReasoningEffortMappers.ForNamedLevels(
            new Dictionary<CompletionReasoningEffort, string> {
                [CompletionReasoningEffort.Disabled] = "off",
                [CompletionReasoningEffort.Low] = "standard",
                [CompletionReasoningEffort.High] = "extended"
            }
        );
        using JsonDocument document = await CaptureRequestAsync(
            "private", effort, WithMapper("private", mapper)
        );
        AssertEffort(document.RootElement, expected);
    }

    [Fact]
    public async Task Catalogs_AreIndependentAndAliasesKeepTheirExactWireId() {
        const string alias = "provider/private:nitro";
        var first = CompletionModelSpecCatalog.Empty.WithModels(
            ["private", alias],
            new CompletionModelSpec {
                ReasoningMapper = new DelegateMapper(_ => new(CompletionReasoningEffort.High, "extended"))
            }
        );
        var second = WithMapper(alias, new DelegateMapper(_ => new(CompletionReasoningEffort.Low, "standard")));
        using JsonDocument firstBody = await CaptureRequestAsync(alias, CompletionReasoningEffort.Medium, first);
        using JsonDocument secondBody = await CaptureRequestAsync(alias, CompletionReasoningEffort.Medium, second);
        AssertEffort(firstBody.RootElement, "extended");
        AssertEffort(secondBody.RootElement, "standard");
        Assert.Equal(alias, firstBody.RootElement.GetProperty("model").GetString());
        Assert.Equal(alias, secondBody.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SelectedSpecWithoutMapper_DoesNotBorrowMapperFromWildcard() {
        var specs = WithMapper("unused", new DelegateMapper(_ => new(CompletionReasoningEffort.High, "extended")))
            .WithPattern("*", new CompletionModelSpec {
                ReasoningMapper = new DelegateMapper(_ => new(CompletionReasoningEffort.High, "extended"))
            })
            .WithModel("private", new CompletionModelSpec { OutputTokenLimit = 123 });
        using JsonDocument document = await CaptureRequestAsync("private", CompletionReasoningEffort.Medium, specs);
        AssertEffort(document.RootElement, "medium");
        Assert.False(document.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task PrefixMapper_MapsUnknownIdWithoutRewritingIt() {
        var specs = CompletionModelSpecCatalog.Empty.WithPattern(
            "provider/*", new CompletionModelSpec {
                ReasoningMapper = new DelegateMapper(_ => new(CompletionReasoningEffort.High, "extended"))
            }
        );
        using JsonDocument document = await CaptureRequestAsync("provider/unknown", CompletionReasoningEffort.Medium, specs);
        AssertEffort(document.RootElement, "extended");
        Assert.Equal("provider/unknown", document.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault, "high")]
    [InlineData((CompletionReasoningEffort)999, "high")]
    [InlineData(CompletionReasoningEffort.High, "")]
    [InlineData(CompletionReasoningEffort.High, " ")]
    [InlineData(CompletionReasoningEffort.High, null)]
    public async Task InvalidOrUnconsumableMapping_FailsBeforeHttp(
        CompletionReasoningEffort effective,
        string? wire
    ) {
        using var handler = new RecordingHandler();
        using var http = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, http, options: new OpenAIChatClientOptions {
            ReasoningEffort = CompletionReasoningEffort.High,
            ModelSpecs = WithMapper("private", new DelegateMapper(_ => new(effective, wire)))
        });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.StreamCompletionAsync(Request("private"), null));
        Assert.Empty(handler.RequestBodies);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, "off")]
    [InlineData(CompletionReasoningEffort.High, "extended")]
    public async Task QwenSwitch_RejectsConfiguredNamesBeforeHttp(
        CompletionReasoningEffort effective,
        string wire
    ) {
        using var handler = new RecordingHandler();
        using var http = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, http, OpenAIChatDialects.QwenSgLang,
            new OpenAIChatClientOptions {
                ReasoningEffort = effective,
                ModelSpecs = WithMapper("private", new DelegateMapper(_ => new(effective, wire)))
            });
        await Assert.ThrowsAsync<ArgumentException>(() => client.StreamCompletionAsync(Request("private"), null));
        Assert.Empty(handler.RequestBodies);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, false)]
    [InlineData(CompletionReasoningEffort.High, true)]
    public async Task QwenSwitch_AcceptsSemanticMappingWithoutPretendingToSendLevels(
        CompletionReasoningEffort effective,
        bool expected
    ) {
        using JsonDocument document = await CaptureRequestAsync(
            "private", CompletionReasoningEffort.Medium,
            WithMapper("private", new DelegateMapper(_ => new(effective, null))),
            OpenAIChatDialects.QwenSgLang
        );
        Assert.Equal(expected, document.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        AssertEffort(document.RootElement, null);
    }

    [Fact]
    public async Task DeepSeekSwitch_RejectsCustomDisabledNameBeforeHttp() {
        using var handler = new RecordingHandler();
        using var http = CreateHttpClient(handler);
        var client = new DeepSeekV4ChatClient(null, http, new OpenAIChatClientOptions {
            ReasoningEffort = CompletionReasoningEffort.Disabled,
            ModelSpecs = WithMapper("private", new DelegateMapper(_ => new(CompletionReasoningEffort.Disabled, "off")))
        });
        await Assert.ThrowsAsync<ArgumentException>(() => client.StreamCompletionAsync(Request("private"), null));
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task DeepSeekSwitch_UsesConfiguredEnabledName() {
        using JsonDocument document = await CaptureRequestAsync(
            "private", CompletionReasoningEffort.High,
            WithMapper("private", new DelegateMapper(_ => new(CompletionReasoningEffort.High, "extended"))),
            OpenAIChatDialects.DeepSeekV4
        );
        AssertEffort(document.RootElement, "extended");
        AssertThinking(document.RootElement, "enabled");
    }

    [Fact]
    public async Task UnsupportedDialect_StillRejectsExplicitEffortBeforeHttp() {
        using var handler = new RecordingHandler();
        using var http = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, http, OpenAIChatDialects.SgLangCompatible,
            new OpenAIChatClientOptions { ReasoningEffort = CompletionReasoningEffort.Low });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StreamCompletionAsync(Request("glm-5.3"), null));
        Assert.Empty(handler.RequestBodies);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault)]
    [InlineData(CompletionReasoningEffort.Disabled)]
    public async Task ForcedChoiceFalse_RejectsIndependentlyOfEffortBeforeHttp(CompletionReasoningEffort effort) {
        using var handler = new RecordingHandler();
        using var http = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, http, options: new OpenAIChatClientOptions {
            ReasoningEffort = effort,
            ModelSpecs = CompletionModelSpecCatalog.Empty.WithModel("private", new CompletionModelSpec {
                ForcedToolChoiceSupported = false
            })
        });
        var tools = new ToolDefinition("lookup", "Lookup data.", new ToolSchema.Object([]));
        var request = new CompletionRequest("private", new CompletionPromptPrefix(
            string.Empty, new CompletionOutputContract([tools], CompletionToolChoice.RequiredAny),
            [new ObservationMessage("Hello.")]), []);
        var exception = await Assert.ThrowsAsync<CompletionRequestRejectedException>(
            () => client.StreamCompletionAsync(request, null)
        );
        Assert.Equal("model_forced_tool_choice_unsupported", exception.Termination.ProviderReason);
        Assert.Empty(handler.RequestBodies);
    }

    private static CompletionModelSpecCatalog WithMapper(string modelId, ICompletionReasoningEffortMapper mapper)
        => CompletionModelSpecCatalog.Empty.WithModel(modelId, new CompletionModelSpec { ReasoningMapper = mapper });

    private static async Task<JsonDocument> CaptureRequestAsync(
        string modelId,
        CompletionReasoningEffort effort,
        CompletionModelSpecCatalog? modelSpecs = null,
        OpenAIChatDialect? dialect = null
    ) {
        using var handler = new RecordingHandler();
        using var http = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, http, dialect, new OpenAIChatClientOptions {
            ReasoningEffort = effort, ModelSpecs = modelSpecs
        });
        CompletionResult result = await client.StreamCompletionAsync(Request(modelId), null);
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        Assert.Equal("ok", result.Message.GetFlattenedText());
        return JsonDocument.Parse(Assert.Single(handler.RequestBodies));
    }

    private static CompletionRequest Request(string modelId) => new(
        modelId, new CompletionPromptPrefix(string.Empty,
            CompletionOutputContract.ProviderDefault([]), [new ObservationMessage("Hello.")]), []
    );

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler) {
        BaseAddress = new Uri("https://example.invalid/")
    };

    private static void AssertEffort(JsonElement body, string? expected) {
        if (expected is null) { Assert.False(body.TryGetProperty("reasoning_effort", out _)); }
        else { Assert.Equal(expected, body.GetProperty("reasoning_effort").GetString()); }
    }

    private static void AssertThinking(JsonElement body, string? expected) {
        if (expected is null) { Assert.False(body.TryGetProperty("thinking", out _)); }
        else { Assert.Equal(expected, body.GetProperty("thinking").GetProperty("type").GetString()); }
    }

    private sealed class DelegateMapper(Func<CompletionReasoningEffort, ReasoningEffortMapping> map)
        : ICompletionReasoningEffortMapper {
        public int Calls { get; private set; }

        public ReasoningEffortMapping Map(CompletionReasoningEffort requested) {
            Calls++;
            return map(requested);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
                        + "data: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream"
                )
            };
        }
    }
}
