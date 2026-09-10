using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

/// <summary>
/// Opt-in production-client replay acceptance. The transport only verifies
/// outgoing native items; it never replaces or repairs the request. Origin,
/// payload and live journals are never modified. Credentials and opaque
/// payloads are never reported.
/// </summary>
public sealed class OpenAICodexReasoningReplayLiveTests {
    private static readonly string[] Models = ["gpt-5.6-sol", "gpt-6-astra", "gpt-5.6-luna"];
    private static readonly ToolDefinition Checkpoint = new(
        "checkpoint", "Record the computed result; no external side effects.",
        new ToolSchema.Object([
            new ToolSchema.Property("answer", new ToolSchema.Value(ToolParamType.String), isRequired: true)
        ], additionalProperties: false));

    [Fact]
    [Trait("Category", "LiveE2E")]
    public async Task LiveE2E_NativeReasoning_ModelSwitchMatrix() {
        if (Environment.GetEnvironmentVariable("ATELIA_RUN_CODEX_REASONING_REPLAY_LIVE") != "1") {
            return;
        }
        string scope = Environment.GetEnvironmentVariable("ATELIA_CODEX_REASONING_REPLAY_SCOPE") ?? "matrix";
        Assert.True(scope is "matrix" or "astra-control", "Unknown replay probe scope.");
        bool controlOnly = scope == "astra-control";
        int expectedCalls = controlOnly ? 3 : 17;
        string[] seedModels = controlOnly ? [Models[1]] : Models;
        string authPath = RequiredAbsolutePath("ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE");
        string reportPath = RequiredAbsolutePath("ATELIA_CODEX_REASONING_REPLAY_REPORT");
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using var report = new FileStream(reportPath, fileOptions);
        var provider = new CodexCliAuthFileCredentialProvider(authPath);
        var credential = await provider.GetCredentialAsync(CancellationToken.None);
        var initial = new ObservationMessage(
            "Compute (17 * 23) + (19 * 29). Call checkpoint once with the decimal answer as a string.");
        var seeds = new Dictionary<string, ActionMessage>();
        int successfulCalls = 0;
        foreach (string model in seedModels) {
            var result = await CallAsync("seed", model, model, [initial], requireTool: true);
            if (result is null) { continue; }
            bool hasEncrypted = result.Blocks.OfType<OpenAIResponsesReasoningBlock>().Any(block => {
                using var payload = JsonDocument.Parse(block.RawItemJson);
                return payload.RootElement.TryGetProperty("encrypted_content", out var encrypted)
                    && encrypted.ValueKind == JsonValueKind.String && encrypted.GetString()!.Length > 0;
            });
            bool hasTool = result.Blocks.OfType<ActionBlock.ToolCall>().Count() == 1
                && result.Blocks.OfType<ActionBlock.ToolCall>().Single().Call.ToolName == "checkpoint";
            await WriteAsync(new { kind = "seed-eligibility", model, hasEncrypted, hasTool });
            if (hasEncrypted && hasTool) { seeds.Add(model, result); }
        }

        // Three diagonal controls plus the four requested directed edges.
        (string Source, string Target)[] pairs = [
            (Models[0], Models[0]), (Models[1], Models[1]), (Models[2], Models[2]),
            (Models[0], Models[1]), (Models[1], Models[0]),
            (Models[0], Models[2]), (Models[2], Models[0])
        ];
        foreach (var (source, target) in pairs) {
            if (controlOnly && (source != Models[1] || target != Models[1])) { continue; }
            if (!seeds.TryGetValue(source, out var seed)) { continue; }
            var call = seed.Blocks.OfType<ActionBlock.ToolCall>().Single().Call;
            var toolResult = new ToolResultsMessage(null, [
                ToolResult.FromText(call.ToolName, call.ToolCallId, ToolExecutionStatus.Success, "Recorded. Reply with exactly OK.")
            ]);
            IHistoryMessage[] currentTurn = [initial, seed, toolResult];
            var continued = await CallAsync("tool-continuation", source, target, currentTurn, requireTool: false);
            if (continued is null || continued.Blocks.OfType<ActionBlock.ToolCall>().Any()) { continue; }
            await CallAsync("next-user-turn", source, target,
                [.. currentTurn, continued, new ObservationMessage("Reply with exactly OK.")], requireTool: false);
        }
        await WriteAsync(new { kind = "summary", scope, successfulCalls, expectedCalls, eligibleSeeds = seeds.Count });
        Assert.True(successfulCalls == expectedCalls && seeds.Count == seedModels.Length,
            "Backend replay probe incomplete or unsuccessful; inspect the metadata-only report. No automatic retries were performed.");

        async Task<ActionMessage?> CallAsync(string stage, string source, string target,
            IHistoryMessage[] history, bool requireTool) {
            var request = new CompletionRequest(target, new CompletionPromptPrefix(
                "Follow the user's instruction. After checkpoint returns, do not call tools again; reply with exactly OK.",
                requireTool
                    ? new CompletionOutputContract([Checkpoint], CompletionToolChoice.RequiredNamed("checkpoint"), allowParallelToolCalls: false)
                    : new CompletionOutputContract([Checkpoint], CompletionToolChoice.None, allowParallelToolCalls: false),
                [.. history]), []);
            var native = history.OfType<ActionMessage>().SelectMany(action => action.Blocks)
                .OfType<OpenAIResponsesReasoningBlock>().ToArray();
            using var handler = new ProbeHandler(native, OpenAICodexResponsesClient.CreateProductionHandler());
            using var client = new OpenAICodexResponsesClient(provider,
                new OpenAICodexResponsesClientOptions {
                    ExpectedAccountFingerprint = credential.AccountFingerprint,
                    Originator = "atelia-live-reasoning-replay", ProductName = "Atelia",
                    ProductVersion = "reasoning-replay-acceptance-v1", MaxConcurrentRequests = 1,
                    ReasoningEffort = CompletionReasoningEffort.Medium
                }, handler);
            var timer = Stopwatch.StartNew();
            string? errorType = null;
            string? adapterFailureReason = null;
            CompletionResult? result = null;
            try {
                result = await client.StreamCompletionAsync(request, observer: null, CancellationToken.None);
            }
            catch (Exception error) {
                // Never serialize exception messages, inner exceptions, provider
                // bodies, source content, OAuth/account identity or opaque items.
                errorType = error.GetType().Name;
                adapterFailureReason = (error as OpenAICodexResponsesException)?.Reason.ToString();
            }
            bool completed = result?.Termination.Kind == CompletionTerminationKind.Completed
                && handler.CompletedEvent && handler.HttpStatus == 200
                && handler.ReportedModel == target && handler.NativeItemsUnchanged;
            await WriteAsync(new {
                kind = "call", stage, source, target, utc = DateTimeOffset.UtcNow,
                elapsedMs = timer.ElapsedMilliseconds, handler.SentCalls, handler.HttpStatus,
                handler.CompletedEvent, handler.ReportedModel, handler.EffectiveContext,
                nativeReasoningItems = native.Length, handler.NativeItemsUnchanged,
                completed, parserTermination = result?.Termination.Kind.ToString(), errorType, adapterFailureReason,
                projection = "production-client-native-input", reasoningContext = "omitted"
            });
            if (completed) { successfulCalls++; }
            return completed ? result!.Message : null;
        }

        async Task WriteAsync<T>(T row) {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row) + "\n");
            await report.WriteAsync(bytes);
            await report.FlushAsync();
        }
    }

    private static string RequiredAbsolutePath(string name) {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null || !Path.IsPathFullyQualified(value)) {
            throw new InvalidOperationException($"{name} must specify an absolute path.");
        }
        return value;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_ProductionInputPreservesNativeItemsAndOtherFields(bool omitContentType) {
        const string payload = """{"type":"reasoning","id":"rs_test","encrypted_content":"synthetic-not-a-secret","summary":[],"future":{"preserve":true}}""";
        var native = new OpenAIResponsesReasoningBlock(payload,
            new CompletionDescriptor("chatgpt.com", ChatGptCodexResponsesProfile.ApiSpecId, Models[0]));
        var input = new JsonArray(JsonNode.Parse(payload));
        var original = JsonNode.Parse("""{"model":"gpt-6-astra","instructions":"fixture","input":[],"reasoning":{"effort":"medium","summary":"auto"},"stream":true,"store":false} """)!;
        original["input"] = input;
        JsonNode expected = original.DeepClone();
        var sink = new FixtureHandler(expected, omitContentType);
        using var handler = new ProbeHandler([native], sink);
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/responses") {
            Content = new StringContent(original.ToJsonString())
        };
        using var response = await invoker.SendAsync(request, CancellationToken.None);
        Assert.True(sink.ExactBody);
        Assert.True(handler.NativeItemsUnchanged);
        Assert.True(handler.CompletedEvent);
        Assert.Equal(1, handler.SentCalls);
        Assert.Equal(Models[1], handler.ReportedModel);
        Assert.Equal("all_turns", handler.EffectiveContext);
        Assert.Equal(FixtureHandler.Terminal, await response.Content.ReadAsStringAsync());
        Assert.Equal(omitContentType, response.Content.Headers.ContentType is null);
        Assert.Equal(Models[0], native.Origin.Model);
        using var second = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/responses");
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(second, CancellationToken.None));
        Assert.Equal(1, handler.SentCalls);
    }

    [Fact]
    public async Task Probe_MissingNativeItemIsNotRepairedOrSent() {
        var native = new OpenAIResponsesReasoningBlock("""{"type":"reasoning","encrypted_content":"fixture"}""",
            new CompletionDescriptor("chatgpt.com", ChatGptCodexResponsesProfile.ApiSpecId, Models[0]));
        using var handler = new ProbeHandler([native], new FixtureHandler(new JsonObject(), false));
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/responses") {
            Content = new StringContent("""{"input":[]}""")
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, CancellationToken.None));
        Assert.False(handler.NativeItemsUnchanged);
        Assert.Equal(0, handler.SentCalls);
    }

    private sealed class FixtureHandler(JsonNode expected, bool omitContentType) : HttpMessageHandler {
        public const string Terminal = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"model\":\"gpt-6-astra\",\"reasoning\":{\"context\":\"all_turns\"}}}\n\n";
        public bool ExactBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            ExactBody = JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct)));
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent(Terminal, Encoding.UTF8, "text/event-stream")
            };
            if (omitContentType) { response.Content.Headers.ContentType = null; }
            return response;
        }
    }

    private sealed class ProbeHandler(OpenAIResponsesReasoningBlock[] native,
        HttpMessageHandler inner) : DelegatingHandler(inner) {
        public int SentCalls { get; private set; }
        public int? HttpStatus { get; private set; }
        public bool NativeItemsUnchanged { get; private set; }
        public bool CompletedEvent { get; private set; }
        public string? ReportedModel { get; private set; }
        public string? EffectiveContext { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (SentCalls != 0) { throw new InvalidOperationException("Probe forbids automatic retries."); }
            byte[] wire = await request.Content!.ReadAsByteArrayAsync(ct);
            using var actual = JsonDocument.Parse(wire);
            var reasoning = actual.RootElement.GetProperty("input").EnumerateArray()
                .Where(item => item.GetProperty("type").GetString() == "reasoning").ToArray();
            NativeItemsUnchanged = reasoning.Length == native.Length;
            for (int i = 0; NativeItemsUnchanged && i < native.Length; i++) {
                using var original = JsonDocument.Parse(native[i].RawItemJson);
                NativeItemsUnchanged &= JsonElement.DeepEquals(original.RootElement, reasoning[i]);
            }
            if (!NativeItemsUnchanged) { throw new InvalidOperationException("Native item wire equality failed."); }
            SentCalls++;
            var response = await base.SendAsync(request, ct);
            HttpStatus = (int)response.StatusCode;
            // Buffer this small synthetic experiment only; hand identical bytes
            // to the production SSE parser after collecting terminal metadata.
            byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
            string? contentType = response.Content.Headers.ContentType?.ToString();
            if (response.IsSuccessStatusCode) {
                foreach (string line in Encoding.UTF8.GetString(responseBytes).Split('\n')) {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) { continue; }
                    string data = line[5..].Trim();
                    if (data == "[DONE]") { continue; }
                    using var frame = JsonDocument.Parse(data);
                    if (frame.RootElement.TryGetProperty("type", out var type)
                        && type.GetString() == "response.completed") {
                        var terminal = frame.RootElement.GetProperty("response");
                        CompletedEvent = terminal.GetProperty("status").GetString() == "completed";
                        ReportedModel = ReadSafeToken(terminal, "model");
                        if (terminal.TryGetProperty("reasoning", out var config) && config.ValueKind == JsonValueKind.Object) {
                            EffectiveContext = ReadSafeToken(config, "context");
                        }
                    }
                }
            }
            response.Content.Dispose();
            response.Content = new ByteArrayContent(responseBytes);
            if (contentType is not null) {
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            return response;
        }

        private static string? ReadSafeToken(JsonElement value, string name) {
            if (!value.TryGetProperty(name, out var token) || token.ValueKind != JsonValueKind.String) { return null; }
            string text = token.GetString()!;
            return text.Length is > 0 and <= 96 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                ? text : "redacted-non-token";
        }
    }
}
