using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;

namespace ToolCallIdMatrix;

// Separate from tool-ID tests. Reasoning and native replay payloads stay in memory;
// receipts contain only counts, comparisons, classification and request allowlists.
sealed class ReasoningTextProbe(MatrixConfig config, string outputRoot) {
    const string Question = "Find the smallest positive integer n such that n % 17 = 5, n % 19 = 7, and n % 23 = 11. Think carefully. Reply with only the integer.";
    readonly List<ReasoningTextAttempt> attempts = [];
    int invocations;

    public async Task Run(CancellationToken cancellation) {
        string runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-reasoning-text-" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(outputRoot, runId);
        Directory.CreateDirectory(directory);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string? commit = Runner.GitCommit();
        try {
            foreach (var configured in config.Endpoints) {
                var defaults = configured with { ReasoningEffort = CompletionReasoningEffort.ProviderDefault };
                var low = configured with { ReasoningEffort = CompletionReasoningEffort.Low };
                var defaultSink = new ReasoningTextRequestSink();
                var lowSink = new ReasoningTextRequestSink();
                var (defaultClient, defaultError) = await Create(defaults, defaultSink, cancellation);
                using (defaultClient) {
                    var first = await Invoke(defaults, defaultClient, defaultSink, defaultError,
                        "default-on", true, true, Request(defaults.ModelId), null, cancellation);
                    await Invoke(defaults, defaultClient, defaultSink, defaultError,
                        "default-off", false, false, Request(defaults.ModelId), null, cancellation);
                    var (lowClient, lowError) = await Create(low, lowSink, cancellation);
                    using (lowClient) {
                        var explicitLow = await Invoke(low, lowClient, lowSink, lowError,
                            "low-on", true, false, Request(low.ModelId), null, cancellation);
                        bool useLow = explicitLow?.Termination.IsSuccess == true;
                        CompletionResult? source = useLow ? explicitLow : first?.Termination.IsSuccess == true ? first : null;
                        var target = useLow ? low : defaults;
                        var replayRequest = source is null ? Request(target.ModelId) : Request(target.ModelId, source.Message);
                        await Invoke(target, useLow ? lowClient : defaultClient, useLow ? lowSink : defaultSink,
                            useLow ? lowError : defaultError, "replay-on", true, false, replayRequest,
                            source is null ? null : useLow ? "low-on" : "default-on", cancellation,
                            missingReplaySource: source is null);
                    }
                }
            }
        }
        finally {
            var receipt = new {
                schema = "reasoning-text-probe-v1", runId, commit, started, finished = DateTimeOffset.UtcNow,
                expectedAnswer = ExpectedAnswer(), plannedInvocations = config.Endpoints.Count * 4,
                config.MaxRequests, config.RequestTimeoutSeconds, invocations,
                httpPosts = attempts.Sum(a => a.HttpPosts), httpOtherRequests = attempts.Sum(a => a.HttpOtherRequests),
                completed = attempts.Count(a => a.Outcome == "completed"),
                readableTextObserved = attempts.Count(a => a.PlainTextCharacters > 0),
                successfulReadableText = attempts.Count(a => a.Outcome == "completed" && a.PlainTextCharacters > 0),
                attempts
            };
            var json = new JsonSerializerOptions(MatrixConfig.Json) { WriteIndented = true };
            await File.WriteAllTextAsync(Path.Combine(directory, "reasoning-text.json"), JsonSerializer.Serialize(receipt, json), new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(directory, "report.md"), Report(runId, commit), new UTF8Encoding(false));
            Console.WriteLine($"Reasoning text evidence: {Path.Combine(directory, "reasoning-text.json")}");
            Console.WriteLine($"Reasoning text report: {Path.Combine(directory, "report.md")}");
        }
    }

    async Task<(ClientLease? Client, string? Error)> Create(Endpoint endpoint, ReasoningTextRequestSink sink, CancellationToken cancellation) {
        if (cancellation.IsCancellationRequested || invocations >= config.MaxRequests) return (null, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
        try { return (await ClientLease.Create(endpoint, deadline.Token, sink), null); }
        catch (Exception error) { return (null, error.GetType().Name); }
    }

    async Task<CompletionResult?> Invoke(Endpoint endpoint, ClientLease? client, ReasoningTextRequestSink sink, string? initializationError,
        string phase, bool requestText, bool defaultOverload, CompletionRequest request, string? replaySource,
        CancellationToken cancellation, bool missingReplaySource = false) {
        var record = new ReasoningTextAttempt {
            EndpointId = endpoint.Id, Kind = endpoint.Kind, ModelId = endpoint.ModelId, Surface = endpoint.Surface,
            Phase = phase, ReasoningEffort = endpoint.ReasoningEffort.ToString(), RequestReasoningText = requestText,
            InvocationOverload = defaultOverload ? "three-parameter-default" : "explicit-options",
            ReplaySourcePhase = replaySource,
            SourceMessageReusedUnchanged = replaySource is null ? null : true,
            SourceReasoningBlocks = replaySource is null ? null : request.TailMessages.OfType<ActionMessage>()
                .Sum(m => m.Blocks.OfType<ActionBlock.ReasoningBlock>().Count())
        };
        attempts.Add(record);
        if (cancellation.IsCancellationRequested) { record.Reason = "host-cancelled-before-invocation"; return null; }
        if (invocations >= config.MaxRequests) { record.Reason = "invocation-budget-exhausted"; return null; }
        if (missingReplaySource) { record.Reason = "no-completed-source-message"; return null; }
        if (client is null) { record.Outcome = "local-configuration"; record.ExceptionType = initializationError; return null; }

        invocations++;
        sink.Reset();
        client.Sink.Reset();
        int postsBefore = client.Counter.Posts, othersBefore = client.Counter.Others;
        var timer = Stopwatch.StartNew();
        var deltas = new StringBuilder();
        var observer = new CompletionStreamObserver();
        observer.ReceivedReasoningDelta += delta => { record.ReasoningDeltaEvents++; deltas.Append(delta); };
        observer.ReceivedThinkingBegin += () => record.ThinkingBeginEvents++;
        observer.ReceivedThinkingEnd += () => record.ThinkingEndEvents++;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
        CompletionResult? result = null;
        try {
            result = defaultOverload
                ? await client.Client.StreamCompletionAsync(request, observer, deadline.Token)
                : await client.Client.StreamCompletionAsync(request,
                    new CompletionInvocationOptions { RequestReasoningText = requestText }, observer, deadline.Token);
            record.Outcome = Probe.Outcome(result);
            record.Termination = result.Termination.Kind.ToString();
            record.FailureKind = result.Failure?.Kind.ToString();
            record.HttpStatusCode = result.Failure?.HttpStatusCode;
            string answer = result.Message.GetFlattenedText().Trim();
            record.AnswerCharacters = answer.Length;
            record.AnswerMatchesExpected = answer == ExpectedAnswer().ToString(System.Globalization.CultureInfo.InvariantCulture)
                && result.Message.ToolCalls.Count == 0;
            var blocks = result.Message.Blocks.OfType<ActionBlock.ReasoningBlock>().ToArray();
            record.ReasoningBlocks = blocks.Length;
            string plainText = string.Concat(blocks.Select(b => b.PlainText));
            record.PlainTextCharacters = plainText.Length;
            record.ReasoningDeltaCharacters = deltas.Length;
            record.DeltaMatchesPlainText = deltas.Length == 0 && plainText.Length == 0
                ? null : string.Equals(deltas.ToString(), plainText, StringComparison.Ordinal);
            record.ReadableTextEvidence = (deltas.Length > 0, plainText.Length > 0) switch {
                (false, false) => "absent",
                (false, true) => "plain-text-only",
                (true, false) => "delta-only",
                _ => record.DeltaMatchesPlainText == true ? "matching-delta-and-plain-text" : "delta-plain-text-mismatch"
            };
            record.Usage = new { result.Usage.UncachedInputTokens, result.Usage.CacheCreationInputTokens,
                result.Usage.CacheReadInputTokens, result.Usage.OutputTokens };
        }
        catch (CompletionFailureException error) {
            record.FailureKind = error.Failure.Kind.ToString();
            record.HttpStatusCode = error.Failure.HttpStatusCode;
            record.ExceptionType = error.GetType().Name;
            record.Outcome = error.Failure.Kind switch {
                CompletionFailureKind.Http => "http-failed", CompletionFailureKind.Provider => "provider-failed", _ => "unknown-transport"
            };
        }
        catch (OperationCanceledException error) {
            record.Outcome = "unknown-cancelled";
            record.ExceptionType = error.GetType().Name;
            record.Reason = cancellation.IsCancellationRequested ? "host-cancelled" : "host-timeout";
        }
        catch (Exception error) {
            record.ExceptionType = error.GetType().Name;
            record.Outcome = client.Counter.Posts > postsBefore ? "unknown-exception" : "local-rejected";
        }
        finally {
            record.ElapsedMilliseconds = timer.ElapsedMilliseconds;
            record.HttpPosts = client.Counter.Posts - postsBefore;
            record.HttpOtherRequests = client.Counter.Others - othersBefore;
            record.ReasoningDeltaCharacters = deltas.Length;
            record.Requests = [.. sink.Exchanges];
            record.RequestEvidenceParseFailures = sink.ParseFailures;
        }
        Console.WriteLine($"reasoning-text:{endpoint.Id}:{phase}: {record.Outcome}; answer={record.AnswerMatchesExpected}; PlainText={record.PlainTextCharacters}; delta={record.ReasoningDeltaCharacters}; POST={record.HttpPosts}");
        return result;
    }

    static CompletionRequest Request(string model, ActionMessage? replay = null) {
        var prefix = new CompletionPromptPrefix("Solve the user's arithmetic problem accurately. Answer with only one integer.",
            CompletionOutputContract.ProviderDefault([]), []);
        var history = new List<IHistoryMessage> { new ObservationMessage(Question) };
        if (replay is not null) {
            history.Add(replay); // Same object and native blocks returned by the source call.
            history.Add(new ObservationMessage("Re-check the earlier answer carefully. Reply with only the smallest positive integer, correcting it if necessary."));
        }
        return new(model, prefix, history);
    }

    static int ExpectedAnswer() {
        for (int value = 1; value <= 17 * 19 * 23; value++)
            if (value % 17 == 5 && value % 19 == 7 && value % 23 == 11) return value;
        throw new InvalidOperationException("No CRT solution in the complete residue interval.");
    }

    string Report(string runId, string? commit) {
        var report = new StringBuilder($"# Reasoning text probe\n\nRun: `{runId}`. Source commit: `{commit}`. Expected answer computed locally: `{ExpectedAnswer()}`.\n\n");
        report.AppendLine($"Logical invocations: {invocations}/{config.MaxRequests}; planned maximum: {config.Endpoints.Count * 4}. No probe retries. Host timeout: {config.RequestTimeoutSeconds} seconds per call.");
        report.AppendLine("\n| Endpoint / model | Phase | Effort | Request text | Outcome | Exact answer | PlainText chars | Delta chars | Delta equals text | Begin/end | Text evidence | Replay source |\n| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var item in attempts)
            report.AppendLine($"| {Cell(item.EndpointId)} / {Cell(item.ModelId)} | {item.Phase} | {item.ReasoningEffort} | {item.RequestReasoningText} | {item.Outcome} | {item.AnswerMatchesExpected} | {item.PlainTextCharacters} | {item.ReasoningDeltaCharacters} | {item.DeltaMatchesPlainText} | {item.ThinkingBeginEvents}/{item.ThinkingEndEvents} | {item.ReadableTextEvidence} | {item.ReplaySourcePhase ?? item.Reason ?? "-"} |");
        report.AppendLine("\nCompleted and readable reasoning are separate evidence. `absent` means no readable content was observed; it does not prove an adapter failure. `plain-text-only` proves a final readable view without streaming deltas. False disables additional text requests and does not redact provider text already returned.");
        report.AppendLine("\nThe JSON contains allowlisted request controls and HTTP counts/status, with no raw answer, reasoning, native replay payload, headers, addresses, credentials, account data, provider error body or exception message. Replay uses the original source Message unchanged; its success does not independently prove which hidden server state was used. Evidence covers only the configured route/model/time and this finite arithmetic probe.");
        return report.ToString();
    }

    static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}

sealed record ReasoningTextAttempt {
    public required string EndpointId { get; init; }
    public required string Kind { get; init; }
    public required string ModelId { get; init; }
    public required string Surface { get; init; }
    public required string Phase { get; init; }
    public required string ReasoningEffort { get; init; }
    public required bool RequestReasoningText { get; init; }
    public required string InvocationOverload { get; init; }
    public DateTimeOffset Started { get; init; } = DateTimeOffset.UtcNow;
    public string Outcome { get; set; } = "not-executed";
    public string? Reason { get; set; }
    public string? Termination { get; set; }
    public string? FailureKind { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? ExceptionType { get; set; }
    public int? AnswerCharacters { get; set; }
    public bool? AnswerMatchesExpected { get; set; }
    public int ReasoningBlocks { get; set; }
    public int PlainTextCharacters { get; set; }
    public int ReasoningDeltaCharacters { get; set; }
    public int ReasoningDeltaEvents { get; set; }
    public int ThinkingBeginEvents { get; set; }
    public int ThinkingEndEvents { get; set; }
    public bool? DeltaMatchesPlainText { get; set; }
    public string ReadableTextEvidence { get; set; } = "unavailable";
    public string? ReplaySourcePhase { get; init; }
    public bool? SourceMessageReusedUnchanged { get; init; }
    public int? SourceReasoningBlocks { get; init; }
    public object? Usage { get; set; }
    public int HttpPosts { get; set; }
    public int HttpOtherRequests { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public int RequestEvidenceParseFailures { get; set; }
    public List<ReasoningTextWireRequest> Requests { get; set; } = [];
}

sealed record ReasoningTextWireRequest(string Method, int? HttpStatusCode, string? Summary, string? Effort,
    bool? IncludeThoughts, string? ThinkingLevel, string? ThinkingDisplay, string? ThinkingType);

sealed class ReasoningTextRequestSink : ICompletionHttpExchangeSink {
    public List<ReasoningTextWireRequest> Exchanges { get; } = [];
    public int ParseFailures { get; private set; }
    public void Reset() { Exchanges.Clear(); ParseFailures = 0; }

    public void OnExchange(CompletionHttpExchange exchange) {
        string? summary = null, effort = null, level = null, display = null, type = null;
        bool? include = null;
        if (exchange.Method == "POST") {
            try {
                using var document = JsonDocument.Parse(exchange.RequestText ?? "");
                var root = document.RootElement;
                var reasoning = Object(root, "reasoning");
                var thinking = Object(root, "thinking");
                var gemini = Object(Object(root, "generationConfig"), "thinkingConfig");
                summary = AllowedString(reasoning, "summary", ["auto", "detailed", "concise"]);
                effort = AllowedString(reasoning, "effort", ["none", "minimal", "low", "medium", "high", "xhigh", "max"])
                    ?? AllowedString(root, "reasoning_effort", ["none", "minimal", "low", "medium", "high", "xhigh", "max"]);
                level = AllowedString(gemini, "thinkingLevel", ["minimal", "low", "medium", "high", "MINIMAL", "LOW", "MEDIUM", "HIGH"]);
                display = AllowedString(thinking, "display", ["summarized", "omitted"]);
                type = AllowedString(thinking, "type", ["enabled", "disabled", "adaptive"]);
                if (gemini.ValueKind == JsonValueKind.Object && gemini.TryGetProperty("includeThoughts", out var flag))
                    include = flag.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException() };
            }
            catch { ParseFailures++; }
        }
        Exchanges.Add(new(exchange.Method, exchange.StatusCode, summary, effort, include, level, display, type));
    }

    static JsonElement Object(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : default;
    static string? AllowedString(JsonElement parent, string name, string[] allowed) {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var child)) return null;
        string? value = child.ValueKind == JsonValueKind.String ? child.GetString() : null;
        return value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : "unexpected-value";
    }
}
