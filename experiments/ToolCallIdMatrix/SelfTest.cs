using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;

namespace ToolCallIdMatrix;

static class SelfTest {
    static int checks;
    public static async Task Run() {
        const string valid = """{"version":1,"endpoints":[{"id":"one","kind":"openai-chat","modelId":"test","baseAddress":"https://example.invalid/v1/","apiKeyEnv":"MATRIX_TEST_KEY"}]}""";
        Check(MatrixConfig.Parse(valid).Endpoints.Count == 1, "config accepted without credentials/network");
        var endpointOne = MatrixConfig.Parse(valid).Endpoints[0];
        var pairs = Probe.ReplayPairs([endpointOne, endpointOne with { Id = "two" }]).ToList();
        Check(pairs.Count == 4 && pairs.Select(p => p.Source.Id + "->" + p.Target.Id).ToHashSet().SetEquals(
            ["one->one", "one->two", "two->one", "two->two"]), "directed matrix includes both directions and diagonals");
        Reject(valid.Replace("\"version\":1", "\"version\":1,\"version\":1"), "duplicate property");
        Reject(valid.Replace("\"version\":1", "\"version\":1,\"apiKey\":\"secret\""), "plaintext secret rejected");
        Reject(valid.Replace("\"version\":1", "\"version\":2"), "unknown version");
        Reject(valid.Replace("\"id\":\"one\"", "\"id\":\"one\",\"isCodex\":true"), "computed property cannot override config");
        Reject(valid.Replace("https://example.invalid/v1/", "https://name:secret@example.invalid/v1/"), "embedded URL credential");
        var request = Probe.Request("model", "call!special:42", "secret-result-only");
        Check(!request.PromptPrefix.SystemPrompt.Contains("secret-result-only") &&
            !((ObservationMessage)request.TailMessages[0]).Content!.Contains("secret-result-only"), "sentinel only in result");
        Check(((ActionMessage)request.TailMessages[1]).ToolCalls.Single().ToolCallId ==
            ((ToolResultsMessage)request.TailMessages[2]).Results.Single().ToolCallId, "call/result same ID");
        Check(Probe.Request("model", null, "sentinel", "required-named").PromptPrefix.OutputContract.ToolChoice.Kind == CompletionToolChoiceKind.RequiredNamed &&
            Probe.Request("model", "call", "sentinel", "required-named").PromptPrefix.OutputContract.ToolChoice.Kind == CompletionToolChoiceKind.ProviderDefault,
            "forced generation choice never leaks into replay");
        var descriptor = new CompletionDescriptor("fixture", "fixture", "test");
        var result = new CompletionResult(new ActionMessage([new ActionBlock.Text("secret-result-only")]), descriptor);
        Check(Probe.Association(result, "secret-result-only"), "exact sentinel accepted");
        Check(!Probe.Association(result with { Termination = CompletionTermination.Incomplete() }, "secret-result-only"), "incomplete rejected");
        Check(!Probe.Association(result with { Message = new ActionMessage([new ActionBlock.Text("secret-result-only"),
            new ActionBlock.ToolCall(new RawToolCall("again", "id", "{}"))]) }, "secret-result-only"), "extra tool call rejected");
        var wire = EvidenceSink.Extract("""{"type":"response.output_item.added","item":{"type":"function_call","id":"item_NOT_CALL","call_id":"call_actual"},"reasoning":{"id":"secret"}}""");
        Check(wire.Count == 1 && wire[0].Id == "call_actual", "Responses item id excluded");
        var ignoredNative = EvidenceSink.Extract("""{"reasoning":{"type":"function_call","call_id":"native_reasoning_secret"},"encrypted_content":"native_reasoning_secret","signature":"native_reasoning_secret"}""");
        Check(ignoredNative.Count == 0 && !JsonSerializer.Serialize(ignoredNative).Contains("native_reasoning_secret"),
            "native payload containing plausible ID fields omitted");
        string[] protocols = [
            """{"messages":[{"role":"assistant","tool_calls":[{"id":"pair_id","type":"function","function":{"name":"matrix_lookup"}}]},{"role":"tool","tool_call_id":"pair_id"}]}""",
            """{"messages":[{"content":[{"type":"tool_use","id":"pair_id"}]},{"content":[{"type":"tool_result","tool_use_id":"pair_id"}]}]}""",
            """{"input":[{"type":"function_call","id":"item_ignored","call_id":"pair_id"},{"type":"function_call_output","call_id":"pair_id"}]}""",
            """{"contents":[{"parts":[{"functionCall":{"id":"pair_id"}}]},{"parts":[{"functionResponse":{"id":"pair_id"}}]}]}"""
        ];
        foreach (var protocol in protocols) {
            var pair = EvidenceSink.Extract(protocol);
            Check(pair.Count == 2 && pair.Count(i => i.Role == "call" && i.Id == "pair_id") == 1 &&
                pair.Count(i => i.Role == "result" && i.Id == "pair_id") == 1, "protocol call/result extraction");
        }
        Check(EvidenceSink.Extract("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"matrix_lookup"}}]}}]}""").Count == 0,
            "missing provider ID never invented");
        var sink = new EvidenceSink();
        var counter = new CountingHandler(new FixtureHandler());
        var builder = new CompletionHttpClientBuilder().UsePrimaryHandler(counter).AddExchangeSink(sink);
        var credential = CodexSubscriptionCredential.Create("fixture-token-not-a-credential", "fixture-account", null, DateTimeOffset.UtcNow.AddHours(1), 1);
        var client = CodexDiagnostics.Create(new FixtureCredentials(credential), new OpenAICodexResponsesClientOptions {
            ExpectedAccountFingerprint = credential.AccountFingerprint
        }, builder);
        using ((IDisposable)client) {
            var completed = await client.StreamCompletionAsync(Probe.Request("gpt-6-luna", "call_fixture", "fixture_result"), null);
            Check(completed.Termination.IsSuccess, "Codex diagnostic injection reaches terminal via fixture");
        }
        Check(counter.Posts == 1, "Codex one real handler send (offline fixture)");
        Check(sink.Exchanges.Count == 1 && sink.Exchanges[0].RequestIds.Count == 2 &&
            sink.Exchanges[0].RequestIds.All(i => i.Id == "call_fixture"), "actual Codex call/result wire IDs captured");
        string sanitized = JsonSerializer.Serialize(sink.Exchanges, MatrixConfig.Json);
        Check(!sanitized.Contains("fixture-token") && !sanitized.Contains("fixture-account") && !sanitized.Contains("native_reasoning_secret"),
            "capture allowlist omits credential/account/reasoning");
        var reasoningSink = new ReasoningTextRequestSink();
        reasoningSink.OnExchange(new("POST", "https://private-address.invalid/", """
            {"reasoning":{"summary":"auto","effort":"low","encrypted_content":"native_reasoning_secret"},
             "thinking":{"display":"omitted","type":"adaptive"},
             "generationConfig":{"thinkingConfig":{"includeThoughts":true,"thinkingLevel":"high"}},
             "messages":[{"content":"prompt_secret"}],"apiKey":"fixture-token"}
            """, 200, "response_secret", "error_secret"));
        var controls = reasoningSink.Exchanges.Single();
        Check(controls.Summary == "auto" && controls.Effort == "low" && controls.IncludeThoughts == true &&
            controls.ThinkingLevel == "high" && controls.ThinkingDisplay == "omitted" && controls.ThinkingType == "adaptive",
            "reasoning probe extracts request controls");
        string reasoningReceipt = JsonSerializer.Serialize(reasoningSink.Exchanges, MatrixConfig.Json);
        Check(!reasoningReceipt.Contains("secret") && !reasoningReceipt.Contains("fixture-token") && !reasoningReceipt.Contains("private-address"),
            "reasoning probe receipt omits raw exchange and secret fields");
        reasoningSink.Reset();
        reasoningSink.OnExchange(new("POST", null, """{"reasoning":{"summary":"prompt_secret"}}""", 200, null, null));
        Check(reasoningSink.Exchanges.Single().Summary == "unexpected-value" &&
            !JsonSerializer.Serialize(reasoningSink.Exchanges).Contains("prompt_secret"), "unknown request control value is not persisted");
        reasoningSink.Reset();
        Check(reasoningSink.Exchanges.Count == 0 && reasoningSink.ParseFailures == 0, "reasoning evidence reset isolates calls");
        Console.WriteLine($"Self-test passed: {checks} checks; only in-memory fixtures, no credentials or network.");
    }

    static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Self-test: " + name); checks++; }
    static void Reject(string text, string name) {
        try { MatrixConfig.Parse(text); }
        catch (Exception error) when (error is InvalidDataException or JsonException) { checks++; return; }
        throw new InvalidOperationException("Self-test rejection missing: " + name);
    }

    sealed class FixtureCredentials(CodexSubscriptionCredential value) : ICodexSubscriptionCredentialProvider {
        public ValueTask<CodexSubscriptionCredential> GetCredentialAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(value);
    }
    sealed class FixtureHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"fixture_result\"}\n\n" +
                    "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"reasoning\":{\"encrypted_content\":\"native_reasoning_secret\"}}}\n\n", Encoding.UTF8, "text/event-stream")
            });
    }
}
