using System.Reflection;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Anthropic;
using Atelia.Completion.Gemini;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Transport;

namespace ToolCallIdMatrix;

sealed class ClientLease(ICompletionClient client, HttpClient? http, EvidenceSink sink, CountingHandler counter) : IDisposable {
    public ICompletionClient Client { get; } = client;
    public EvidenceSink Sink { get; } = sink;
    public CountingHandler Counter { get; } = counter;
    public void Dispose() { (Client as IDisposable)?.Dispose(); http?.Dispose(); }

    public static async Task<ClientLease> Create(Endpoint endpoint, CancellationToken token, ICompletionHttpExchangeSink? additionalSink = null) {
        var sink = new EvidenceSink();
        var counter = new CountingHandler(new HttpClientHandler { AllowAutoRedirect = false });
        var builder = new CompletionHttpClientBuilder().UsePrimaryHandler(counter).AddExchangeSink(sink);
        if (additionalSink is not null) builder.AddExchangeSink(additionalSink);
        HttpClient? http = null;
        try {
            if (endpoint.IsCodex) {
                ICodexSubscriptionCredentialProvider credentials = endpoint.CodexAuthFile is null
                    ? new CodexCliAuthFileCredentialProvider() : new CodexCliAuthFileCredentialProvider(endpoint.CodexAuthFile);
                var snapshot = await credentials.GetCredentialAsync(token);
                string fingerprint = endpoint.CodexAccountFingerprintEnv is null ? snapshot.AccountFingerprint
                    : Environment.GetEnvironmentVariable(endpoint.CodexAccountFingerprintEnv)
                        ?? throw new InvalidOperationException("Codex account fingerprint environment variable missing.");
                var options = new OpenAICodexResponsesClientOptions {
                    ExpectedAccountFingerprint = fingerprint, ReasoningEffort = endpoint.ReasoningEffort,
                    Originator = "atelia", MaxConcurrentRequests = 1
                };
                var codex = CodexDiagnostics.Create(credentials, options, builder);
                return new(codex, null, sink, counter);
            }
            var connection = endpoint.Resolve();
            http = builder.Build();
            http.BaseAddress = new Uri(connection.BaseAddress.TrimEnd('/') + '/');
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Atelia-ToolCallIdMatrix/1.0");
            ICompletionClient client = endpoint.Kind switch {
                "openai-chat" => new OpenAIChatClient(connection.ApiKey, http, endpoint.Surface switch {
                    "openai-chat/strict" => OpenAIChatDialects.Strict,
                    "openai-chat/sglang-compatible" => OpenAIChatDialects.SgLangCompatible,
                    "openai-chat/qwen-sglang" => OpenAIChatDialects.QwenSgLang,
                    "openai-chat/deepseek-v4" => OpenAIChatDialects.DeepSeekV4,
                    _ => throw new NotSupportedException()
                }, new OpenAIChatClientOptions { ReasoningEffort = endpoint.ReasoningEffort }),
                "openai-responses" => new OpenAIResponsesClient(connection.ApiKey, http,
                    new OpenAIResponsesClientOptions { ReasoningEffort = endpoint.ReasoningEffort }),
                "anthropic" => new AnthropicClient(connection.ApiKey, http, enablePromptCaching: false, reasoningEffort: endpoint.ReasoningEffort),
                "gemini" => new GeminiClient(connection.ApiKey, http, new GeminiClientOptions { ReasoningEffort = endpoint.ReasoningEffort }),
                _ => throw new NotSupportedException()
            };
            return new(client, http, sink, counter);
        }
        catch { http?.Dispose(); counter.Dispose(); throw; }
    }
}

static class CodexDiagnostics {
    // Two existing diagnostic seams only; no fields or authentication/routing invariants are changed.
    public static ICompletionClient Create(ICodexSubscriptionCredentialProvider credentials, OpenAICodexResponsesClientOptions options,
        CompletionHttpClientBuilder builder) {
        var build = typeof(CompletionHttpClientBuilder).GetMethod("BuildHandler", BindingFlags.Instance | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null) ?? throw new NotSupportedException("Missing source-local diagnostic handler builder.");
        var constructor = typeof(OpenAICodexResponsesClient).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(ICodexSubscriptionCredentialProvider), typeof(OpenAICodexResponsesClientOptions), typeof(HttpMessageHandler)], null)
            ?? throw new NotSupportedException("Missing source-local Codex diagnostic constructor.");
        var handler = (HttpMessageHandler)build.Invoke(builder, null)!;
        try { return (ICompletionClient)constructor.Invoke([credentials, options, handler]); }
        catch { handler.Dispose(); throw; }
    }
}
