using System.Text.Json;
using Atelia.Completion.Transport;

namespace ToolCallIdMatrix;

sealed record WireId(string Role, string Id, string Field);
sealed record ExchangeEvidence(string Method, int? Status, List<WireId> RequestIds, List<WireId> ResponseIds);

// The full exchange exists only transiently in the library's in-memory capture.
// Persist only this allowlist; never persist headers, URLs, errors, arguments or reasoning.
sealed class EvidenceSink : ICompletionHttpExchangeSink {
    public List<ExchangeEvidence> Exchanges { get; } = [];
    public int ParseFailures { get; private set; }
    public void OnExchange(CompletionHttpExchange exchange) {
        try {
            var requestIds = Extract(exchange.RequestText, out int requestErrors);
            var responseIds = Extract(exchange.ResponseText, out int responseErrors);
            ParseFailures += requestErrors + responseErrors;
            Exchanges.Add(new(exchange.Method, exchange.StatusCode,
                requestIds, responseIds));
        }
        catch { ParseFailures++; }
    }

    public void Reset() { Exchanges.Clear(); ParseFailures = 0; }

    public static List<WireId> Extract(string? text) => Extract(text, out _);

    static List<WireId> Extract(string? text, out int failures) {
        failures = 0;
        var ids = new List<WireId>();
        if (string.IsNullOrWhiteSpace(text)) return ids;
        if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')) { if (!Parse(text, ids)) failures++; }
        else {
            foreach (string line in text.Split('\n')) {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line[5..].Trim();
                if (payload.Length > 0 && payload != "[DONE]" && !Parse(payload, ids)) failures++;
            }
        }
        return ids.Distinct().ToList();
    }

    static bool Parse(string text, List<WireId> ids) {
        try { using var doc = JsonDocument.Parse(text); Visit(doc.RootElement, ids); return true; }
        catch (JsonException) { return false; }
    }

    static void Visit(JsonElement node, List<WireId> ids) {
        if (node.ValueKind == JsonValueKind.Array) {
            foreach (var child in node.EnumerateArray()) Visit(child, ids);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;
        string? type = String(node, "type");
        if (type == "tool_use") Add(node, "id", "call", "tool_use.id", ids);
        if (type == "tool_result") Add(node, "tool_use_id", "result", "tool_result.tool_use_id", ids);
        if (type == "function_call") Add(node, "call_id", "call", "function_call.call_id", ids);
        if (type == "function_call_output") Add(node, "call_id", "result", "function_call_output.call_id", ids);
        if (String(node, "role") == "tool") Add(node, "tool_call_id", "result", "tool.tool_call_id", ids);
        if (node.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            foreach (var call in calls.EnumerateArray()) Add(call, "id", "call", "tool_calls.id", ids);
        if (node.TryGetProperty("functionCall", out var functionCall)) Add(functionCall, "id", "call", "functionCall.id", ids);
        if (node.TryGetProperty("functionResponse", out var functionResponse)) Add(functionResponse, "id", "result", "functionResponse.id", ids);
        foreach (var property in node.EnumerateObject()) {
            // Do not traverse native opaque reasoning payloads.
            if (property.Name is "thinking" or "reasoning" or "encrypted_content" or "signature" or "thoughtSignature") continue;
            Visit(property.Value, ids);
        }
    }

    static string? String(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static void Add(JsonElement node, string name, string role, string field, List<WireId> ids) {
        if (String(node, name) is { Length: > 0 } id) ids.Add(new(role, id, field));
    }
}

sealed class CountingHandler : DelegatingHandler {
    public int Posts { get; private set; }
    public int Others { get; private set; }
    public CountingHandler(HttpMessageHandler inner) => InnerHandler = inner;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        if (request.Method == HttpMethod.Post) Posts++; else Others++;
        return base.SendAsync(request, token);
    }
}

