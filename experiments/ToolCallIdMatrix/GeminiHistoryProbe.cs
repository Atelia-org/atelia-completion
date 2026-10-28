using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Gemini;

namespace ToolCallIdMatrix;

// Separate from the ID-only matrix: continue actual model-produced messages,
// including native blocks, both inside a tool loop and after a completed turn.
sealed class GeminiHistoryProbe(MatrixConfig config, string outputRoot) {
    const string ImportSignature = "skip_thought_signature_validator";
    int invocations, posts, others, passed;

    public async Task Run(CancellationToken cancellation) {
        var target = config.Endpoints.Single(e => e.Kind == "gemini");
        string runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-history-" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(outputRoot, runId);
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(Path.Combine(directory, "history.jsonl")) { AutoFlush = true };
        await log.WriteLineAsync(JsonSerializer.Serialize(new { type = "run", runId, commit = Runner.GitCommit(), probe = "actual-message-gemini-history-v1",
            plannedInvocations = config.Endpoints.Count * 4, config.MaxRequests, config.RequestTimeoutSeconds,
            endpoints = config.Endpoints.Select(e => new { e.Id, e.Kind, e.ModelId, e.GenerationToolChoice }) }, MatrixConfig.Json));
        using var targetClient = await Create(target, cancellation);
        foreach (var source in config.Endpoints) {
            cancellation.ThrowIfCancellationRequested();
            using var sourceClient = await Create(source, cancellation);
            string token = "history_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var generation = Probe.Request(source.ModelId, null, token, source.GenerationToolChoice);
            var seed = await Invoke(sourceClient, source, source, "generation", generation, null, null, false, log, cancellation);
            if (seed is null || !seed.Termination.IsSuccess || seed.Message.ToolCalls.Count != 1 ||
                seed.Message.ToolCalls[0].ToolName != Probe.ToolName) continue;
            var call = seed.Message.ToolCalls.Single();
            bool sourceIdOnWire = sourceClient.Sink.Exchanges.Any(e => e.ResponseIds.Any(i => i.Role == "call" && i.Id == call.ToolCallId));
            var replayTemplate = Probe.Request(source.ModelId, call.ToolCallId, token);
            List<IHistoryMessage> history = [generation.TailMessages[0], seed.Message, replayTemplate.TailMessages[2]];
            var currentRequest = new CompletionRequest(target.ModelId, replayTemplate.PromptPrefix, history);
            await Invoke(targetClient, source, target, "current-turn", currentRequest, token, call.ToolCallId, sourceIdOnWire, log, cancellation);

            // Complete the source turn with its authentic native replay first.
            var completed = await Invoke(sourceClient, source, source, "source-completion",
                new(source.ModelId, replayTemplate.PromptPrefix, history), token, call.ToolCallId, sourceIdOnWire, log, cancellation);
            if (completed is null || !Probe.Association(completed, token)) continue;
            history.Add(completed.Message);
            history.Add(new ObservationMessage("Repeat only the token from the earlier matrix_lookup tool result. Do not call a tool."));
            await Invoke(targetClient, source, target, "next-turn",
                new(target.ModelId, replayTemplate.PromptPrefix, history), token, call.ToolCallId, sourceIdOnWire, log, cancellation);
        }
        await log.WriteLineAsync(JsonSerializer.Serialize(new { type = "summary", invocations, httpPosts = posts,
            httpOtherRequests = others, verifiedReplays = passed }, MatrixConfig.Json));
        Console.WriteLine($"History evidence: {Path.Combine(directory, "history.jsonl")}");
    }

    async Task<ClientLease> Create(Endpoint endpoint, CancellationToken cancellation) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
        return await ClientLease.Create(endpoint, deadline.Token);
    }

    async Task<CompletionResult?> Invoke(ClientLease client, Endpoint source, Endpoint target, string phase,
        CompletionRequest request, string? token, string? callId, bool sourceIdOnWire, StreamWriter log, CancellationToken cancellation) {
        if (invocations >= config.MaxRequests) throw new InvalidOperationException("Invocation budget exhausted.");
        cancellation.ThrowIfCancellationRequested();
        invocations++;
        client.Sink.Reset();
        int beforePosts = client.Counter.Posts, beforeOther = client.Counter.Others;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
        CompletionResult? result = null;
        string outcome;
        string? exceptionType = null;
        CompletionFailureInfo? failure = null;
        try {
            result = await client.Client.StreamCompletionAsync(request, null, deadline.Token);
            outcome = Probe.Outcome(result);
            failure = result.Failure;
        }
        catch (CompletionFailureException error) {
            outcome = error.Failure.Kind == CompletionFailureKind.Http ? "http-failed" : "unknown-transport";
            failure = error.Failure; exceptionType = error.GetType().Name;
        }
        catch (OperationCanceledException error) { outcome = "unknown-cancelled"; exceptionType = error.GetType().Name; }
        catch (Exception error) { outcome = client.Counter.Posts == beforePosts ? "local-rejected" : "unknown-exception"; exceptionType = error.GetType().Name; }

        int httpPosts = client.Counter.Posts - beforePosts, httpOther = client.Counter.Others - beforeOther;
        posts += httpPosts; others += httpOther;
        bool association = token is not null && result is not null && Probe.Association(result, token);
        var exchanges = client.Sink.Exchanges;
        var sent = exchanges.Where(e => e.Method == "POST").ToArray();
        bool pairPreserved = callId is not null && sent.Length > 0 && sent.All(e => e.RequestIds.Count == 2 &&
            e.RequestIds.Count(i => i.Role == "call" && i.Id == callId) == 1 &&
            e.RequestIds.Count(i => i.Role == "result" && i.Id == callId) == 1);
        // These are only compared in memory. Receipts record booleans/counts.
        var nativeSignatures = request.TailMessages.OfType<ActionMessage>().SelectMany(m => m.Blocks.OfType<GeminiReplayBlock>())
            .Where(b => result is not null && b.Origin.ProviderId == result.Invocation.ProviderId && b.Origin.ApiSpecId == result.Invocation.ApiSpecId)
            .SelectMany(ReadSignatures).ToArray();
        bool? nativePreserved = nativeSignatures.Length == 0 ? null : nativeSignatures.SequenceEqual(client.Sink.RequestSignatures);
        int importMarkers = client.Sink.RequestSignatures.Count(s => s == ImportSignature);
        bool signatureEvidence = target.Kind != "gemini" || nativePreserved == true ||
            nativeSignatures.Length == 0 && importMarkers == 1 && client.Sink.RequestSignatures.Count == 1;
        bool verified = sourceIdOnWire && association && pairPreserved && signatureEvidence && client.Sink.ParseFailures == 0;
        if (verified) passed++;
        await log.WriteLineAsync(JsonSerializer.Serialize(new { type = "attempt", phase, source = source.Id, target = target.Id,
            outcome, exceptionType, failure, callId, sourceIdOnWire, pairPreserved, association, verified,
            nativeSignatureCount = nativeSignatures.Length, nativePreserved, importMarkers, httpPosts, httpOther,
            evidenceParseFailures = client.Sink.ParseFailures, exchanges,
            parsedCallIds = result?.Message.ToolCalls.Select(c => c.ToolCallId), invocation = result?.Invocation,
            usage = result is null ? null : new { result.Usage.UncachedInputTokens, result.Usage.CacheReadInputTokens,
                result.Usage.CacheCreationInputTokens, result.Usage.OutputTokens } }, MatrixConfig.Json));
        Console.WriteLine($"{phase}:{source.Id}->{target.Id}: {outcome}; verified={verified}; POST={httpPosts}; nativePreserved={nativePreserved}; importMarkers={importMarkers}");
        return result;
    }

    static string[] ReadSignatures(GeminiReplayBlock block) {
        using var doc = JsonDocument.Parse(block.OpaquePayload);
        return doc.RootElement.GetProperty("parts").EnumerateArray().Where(p => p.TryGetProperty("thoughtSignature", out _))
            .Select(p => p.GetProperty("thoughtSignature").GetString()!).ToArray();
    }
}
