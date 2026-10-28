using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace ToolCallIdMatrix;

static class Program {
    public static async Task<int> Main(string[] args) {
        try {
            if (args.SequenceEqual(["--self-test"])) { await SelfTest.Run(); return 0; }
            if (args.Length is < 1 or > 2 || args.Length == 2 && args[0] is not ("--validate" or "--live")) {
                Console.WriteLine("Usage: ToolCallIdMatrix [--validate|--live] config.json | --self-test");
                return 2;
            }
            bool live = args.Length == 2 && args[0] == "--live";
            string path = Path.GetFullPath(args[^1]);
            var config = MatrixConfig.Parse(await File.ReadAllTextAsync(path));
            int planned = config.Endpoints.Count + config.Endpoints.Count * config.Endpoints.Count;
            Console.WriteLine($"Valid config: {config.Endpoints.Count} endpoints; {planned} planned invocations; budget {config.MaxRequests}.");
            if (!live) { Console.WriteLine("Offline validation only. Credentials were not read; no network calls."); return 0; }
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            string output = Path.GetFullPath(config.OutputDirectory, Path.GetDirectoryName(path)!);
            await new Runner(config, output).Run(cancellation.Token);
            return 0;
        }
        catch (Exception error) {
            // Do not write exception messages: credentials/URLs/provider bodies may occur there.
            Console.Error.WriteLine($"Stopped: {error.GetType().Name}. No exception payload was logged.");
            return 1;
        }
    }
}

sealed record Attempt {
    public required string Id { get; init; }
    public required string Phase { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public DateTimeOffset Started { get; init; } = DateTimeOffset.UtcNow;
    public string Outcome { get; set; } = "not-executed";
    public string? Reason { get; set; }
    public string? ExceptionType { get; set; }
    public string? SampleId { get; set; }
    public bool SampleWireVerified { get; set; }
    public bool? RequestPairPreserved { get; set; }
    public bool? AssociationVerified { get; set; }
    public bool VerifiedCompatible { get; set; }
    public string? Termination { get; set; }
    public CompletionDescriptor? Invocation { get; set; }
    public CompletionFailureInfo? Failure { get; set; }
    public object? Usage { get; set; }
    public List<string> ParsedCallIds { get; set; } = [];
    public List<ExchangeEvidence> Exchanges { get; set; } = [];
    public int HttpPosts { get; set; }
    public int HttpOtherRequests { get; set; }
    public int EvidenceParseFailures { get; set; }
    public long ElapsedMilliseconds { get; set; }
}

static class Probe {
    public const string ToolName = "matrix_lookup";
    public static readonly ToolDefinition Tool = new(ToolName, "Read a harmless compatibility-test token.",
        new ToolSchema.Object([new ToolSchema.Property("key", new ToolSchema.Value(ToolParamType.String), true)]));

    public static CompletionRequest Request(string model, string? id, string sentinel, string generationToolChoice = "provider-default") {
        string system = "You are running a harmless tool transport test. If no tool result exists, call matrix_lookup exactly once with key matrix. " +
            "When its result exists, do not call another tool. Reply with only the token string found in that tool result, verbatim, without quotes or commentary.";
        var choice = id is not null ? CompletionToolChoice.ProviderDefault : generationToolChoice switch {
            "required-any" => CompletionToolChoice.RequiredAny,
            "required-named" => CompletionToolChoice.RequiredNamed(ToolName),
            "provider-default" => CompletionToolChoice.ProviderDefault,
            _ => throw new InvalidOperationException("Unknown generation tool choice.")
        };
        var prefix = new CompletionPromptPrefix(system, new CompletionOutputContract([Tool], choice), []);
        var history = new List<IHistoryMessage> { new ObservationMessage("Read the matrix lookup and report the returned token.") };
        if (id is not null) {
            history.Add(new ActionMessage([new ActionBlock.ToolCall(new RawToolCall(ToolName, id, "{\"key\":\"matrix\"}"))]));
            history.Add(new ToolResultsMessage(null, [ToolResult.FromText(ToolName, id, ToolExecutionStatus.Success,
                JsonSerializer.Serialize(new { token = sentinel }))]));
        }
        return new(model, prefix, history);
    }

    public static bool Association(CompletionResult result, string sentinel) => result.Termination.IsSuccess &&
        result.Message.ToolCalls.Count == 0 && result.Message.GetFlattenedText().Trim() == sentinel;

    public static string Outcome(CompletionResult result) => result.Termination.Kind switch {
        CompletionTerminationKind.Completed => "completed",
        CompletionTerminationKind.Incomplete => "incomplete",
        CompletionTerminationKind.Failed => "provider-failed",
        _ => "unknown"
    };

    public static IEnumerable<(Endpoint Source, Endpoint Target)> ReplayPairs(IReadOnlyList<Endpoint> endpoints) {
        foreach (var source in endpoints)
            foreach (var target in endpoints)
                yield return (source, target);
    }
}

sealed class Runner(MatrixConfig config, string outputRoot) {
    readonly List<Attempt> records = [];
    readonly Dictionary<string, ClientLease> clients = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> clientErrors = new(StringComparer.Ordinal);
    int invocations;

    public async Task Run(CancellationToken cancellation) {
        string runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(outputRoot, runId);
        Directory.CreateDirectory(directory);
        await using var log = new StreamWriter(Path.Combine(directory, "attempts.jsonl"), append: false, new UTF8Encoding(false)) { AutoFlush = true };
        await log.WriteLineAsync(JsonSerializer.Serialize(new { type = "run", runId, started = DateTimeOffset.UtcNow,
            commit = GitCommit(), probe = "synthetic-id-format-v1", plannedInvocations = config.Endpoints.Count * (config.Endpoints.Count + 1),
            config.MaxRequests, config.RequestTimeoutSeconds,
            endpoints = config.Endpoints.Select(e => new { e.Id, e.Kind, e.ModelId, surface = e.Surface, e.ReasoningEffort, e.GenerationToolChoice }) }, MatrixConfig.Json));
        string sentinel = "matrix_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        try {
            foreach (var endpoint in config.Endpoints) {
                try {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    deadline.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
                    clients.Add(endpoint.Id, await ClientLease.Create(endpoint, deadline.Token));
                }
                catch (Exception error) { clientErrors.Add(endpoint.Id, error.GetType().Name); }
            }
            var samples = new Dictionary<string, Attempt>(StringComparer.Ordinal);
            foreach (var endpoint in config.Endpoints) {
                var record = await Invoke(endpoint, endpoint, null, sentinel, "generation", cancellation);
                samples.Add(endpoint.Id, record);
                await Save(record, log);
            }
            foreach (var (source, target) in Probe.ReplayPairs(config.Endpoints)) {
                var sample = samples[source.Id];
                Attempt record;
                if (sample.SampleId is null) record = New("replay", source, target) with { Reason = "source-has-no-single-tool-call-sample" };
                else {
                    record = await Invoke(source, target, sample, sentinel, "replay", cancellation);
                }
                await Save(record, log);
            }
        }
        finally {
            foreach (var client in clients.Values) client.Dispose();
            await log.WriteLineAsync(JsonSerializer.Serialize(new { type = "summary", invocations,
                httpPosts = records.Sum(r => r.HttpPosts), httpOtherRequests = records.Sum(r => r.HttpOtherRequests),
                completed = records.Count(r => r.Outcome == "completed"), verifiedCompatible = records.Count(r => r.VerifiedCompatible),
                finished = DateTimeOffset.UtcNow }, MatrixConfig.Json));
            await File.WriteAllTextAsync(Path.Combine(directory, "report.md"), Report(runId), new UTF8Encoding(false));
            Console.WriteLine($"Report: {Path.Combine(directory, "report.md")}");
        }
    }

    Attempt New(string phase, Endpoint source, Endpoint target) => new() {
        Id = $"{phase}:{source.Id}->{target.Id}", Phase = phase, Source = source.Id, Target = target.Id
    };

    async Task<Attempt> Invoke(Endpoint source, Endpoint target, Attempt? sample, string sentinel, string phase, CancellationToken cancellation) {
        var record = New(phase, source, target) with { SampleId = sample?.SampleId, SampleWireVerified = sample?.SampleWireVerified ?? false };
        if (cancellation.IsCancellationRequested) { record.Reason = "host-cancelled-before-invocation"; return record; }
        if (invocations >= config.MaxRequests) { record.Reason = "invocation-budget-exhausted"; return record; }
        if (!clients.TryGetValue(target.Id, out var client)) {
            record.Outcome = "local-configuration"; record.ExceptionType = clientErrors[target.Id]; return record;
        }
        invocations++;
        client.Sink.Reset();
        int postsBefore = client.Counter.Posts, othersBefore = client.Counter.Others;
        var timer = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
        try {
            var result = await client.Client.StreamCompletionAsync(Probe.Request(target.ModelId, sample?.SampleId, sentinel,
                target.GenerationToolChoice), null, deadline.Token);
            record.Outcome = Probe.Outcome(result);
            record.Termination = result.Termination.Kind.ToString();
            record.Invocation = result.Invocation;
            record.Failure = result.Failure;
            record.Usage = new { result.Usage.UncachedInputTokens, result.Usage.CacheCreationInputTokens,
                result.Usage.CacheReadInputTokens, result.Usage.OutputTokens };
            record.ParsedCallIds = result.Message.ToolCalls.Select(c => c.ToolCallId).ToList();
            if (phase == "generation" && result.Termination.IsSuccess && result.Message.ToolCalls.Count == 1 &&
                result.Message.ToolCalls[0].ToolName == Probe.ToolName && !string.IsNullOrWhiteSpace(result.Message.ToolCalls[0].ToolCallId))
                record.SampleId = result.Message.ToolCalls[0].ToolCallId;
            if (phase == "replay") record.AssociationVerified = Probe.Association(result, sentinel);
        }
        catch (CompletionFailureException error) {
            record.Failure = error.Failure;
            record.ExceptionType = error.GetType().Name;
            record.Outcome = error.Failure.Kind switch { CompletionFailureKind.Http => "http-failed", CompletionFailureKind.Provider => "provider-failed", _ => "unknown-transport" };
        }
        catch (OperationCanceledException error) {
            record.Outcome = "unknown-cancelled"; record.ExceptionType = error.GetType().Name;
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
            record.Exchanges = [.. client.Sink.Exchanges];
            record.EvidenceParseFailures = client.Sink.ParseFailures;
        }
        if (phase == "generation") {
            record.SampleWireVerified = record.SampleId is not null && record.Exchanges.Any(e =>
                e.ResponseIds.Any(i => i.Role == "call" && i.Id == record.SampleId));
            if (record.SampleId is not null && !record.SampleWireVerified) record.Reason = "parser-id-not-proven-on-wire";
        }
        else {
            var posts = record.Exchanges.Where(e => e.Method == "POST").ToList();
            record.RequestPairPreserved = posts.Count > 0 && posts.All(e =>
                e.RequestIds.Count(i => i.Role == "call" && i.Id == record.SampleId) == 1 &&
                e.RequestIds.Count(i => i.Role == "result" && i.Id == record.SampleId) == 1 && e.RequestIds.Count == 2);
            record.VerifiedCompatible = record.Outcome == "completed" && record.AssociationVerified == true &&
                record.RequestPairPreserved == true && record.SampleWireVerified;
        }
        return record;
    }

    async Task Save(Attempt record, StreamWriter log) {
        records.Add(record);
        await log.WriteLineAsync(JsonSerializer.Serialize(record, MatrixConfig.Json));
        Console.WriteLine($"{record.Id}: {record.Outcome}; verified={record.VerifiedCompatible}; POST={record.HttpPosts}; other={record.HttpOtherRequests}");
    }

    string Report(string runId) {
        var text = new StringBuilder($"# Tool-call ID compatibility probe\n\nRun: `{runId}`\n\n");
        text.AppendLine("This is a synthetic ID-format probe. It does not establish complete native-history or reasoning replay compatibility, nor cache hit benefits. No ID mappings are applied.\n");
        text.AppendLine($"Logical invocations: {invocations}/{config.MaxRequests}. HTTP POST attempts: {records.Sum(r => r.HttpPosts)}; other HTTP requests (e.g. capability queries): {records.Sum(r => r.HttpOtherRequests)}. Failures count; no runner retries. Codex's existing credential-reload flow may send a second POST.\n");
        text.AppendLine("## Generated samples\n\n| Source | ID (JSON escaped) | Wire verified | Outcome |\n|---|---|---|---|");
        foreach (var row in records.Where(r => r.Phase == "generation"))
            text.AppendLine($"| {row.Source} | `{JsonSerializer.Serialize(row.SampleId).Replace("|", "\\u007c")}` | {row.SampleWireVerified} | {row.Outcome} |");
        text.AppendLine("\n## Directed matrix (row source → column target)\n");
        text.AppendLine("| Source | " + string.Join(" | ", config.Endpoints.Select(e => e.Id)) + " |");
        text.AppendLine("|---|" + string.Join("|", config.Endpoints.Select(_ => "---")) + "|");
        foreach (var source in config.Endpoints) {
            text.Append("| " + source.Id + " |");
            foreach (var target in config.Endpoints) {
                var row = records.FirstOrDefault(r => r.Phase == "replay" && r.Source == source.Id && r.Target == target.Id);
                text.Append(" " + (row is null ? "not-executed" : row.VerifiedCompatible ? "PASS" : row.Outcome == "completed" ? "completed / unverified" : row.Outcome) + " |");
            }
            text.AppendLine();
        }
        text.AppendLine("\nPASS requires Completed, no new tool calls, exact random token recovered only from the tool result, real source ID proven on wire, and both outbound call/result IDs unchanged. Other outcomes are not evidence of ID incompatibility.\n");
        text.AppendLine("Gemini native targets currently require GeminiReplayBlock and reject this deliberately synthetic history locally. Such cells are untested format compatibility, not endpoint rejections. HTTP/provider/transport failures need controlled follow-up; this runner does not classify natural-language errors.\n");
        text.AppendLine("Detailed statuses, wire ID fields, HTTP statuses, structured failure facts and usage: [attempts.jsonl](attempts.jsonl). Headers, keys, URLs, raw prompts, response text and reasoning are not written.\n");
        return text.ToString();
    }

    static string? GitCommit() {
        try {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            string? output = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit();
            return output is { Length: 40 } && output.All(Uri.IsHexDigit) ? output : null;
        }
        catch { return null; }
    }
}
