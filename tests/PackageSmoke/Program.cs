using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.OpenAI;
using Atelia.Completion.Tools;
using Atelia.Completion.Transport;

var request = new CompletionRequest("package-smoke",
    new CompletionPromptPrefix("Echo only.", CompletionOutputContract.ProviderDefault([]), []),
    [new ObservationMessage("hello")]);

foreach (var (finish, expected) in new[] {
    ("stop", CompletionTerminationKind.Completed),
    ("length", CompletionTerminationKind.Incomplete)
}) {
    using var handler = new ControlledHandler(Frame("hello", finish));
    using var http = CreateHttp(handler);
    var client = new OpenAIChatClient(null, http, OpenAIChatDialects.SgLangCompatible);
    var result = await client.StreamCompletionAsync(request, null);
    Require(result.Termination.Kind == expected, "Wrong completion status.");
    Require(result.Message.GetFlattenedText() == "hello", "Text lost in package consumer.");
    Require(result.Usage.UncachedInputTokens is null && result.Usage.OutputTokens is null
        && result.Usage.CacheCreationInputTokens is null && result.Usage.CacheReadInputTokens is null,
        "Unknown usage was invented.");
    CheckWire(handler);
}

using (var handler = new ControlledHandler("data: {\"error\":{\"message\":\"fixture failure\",\"type\":\"server_error\"}}\n\n"))
using (var http = CreateHttp(handler)) {
    var result = await new OpenAIChatClient(null, http, OpenAIChatDialects.SgLangCompatible)
        .StreamCompletionAsync(request, null);
    Require(result.Termination.Kind == CompletionTerminationKind.Failed, "Provider failure became success.");
    Require(result.Failure is { Kind: CompletionFailureKind.Provider, ProviderCode: "server_error" },
        "Provider failure facts were lost.");
}

using (var handler = new ControlledHandler("{\"error\":{\"code\":\"rate_limit_exceeded\"}}", HttpStatusCode.TooManyRequests))
using (var http = CreateHttp(handler)) {
    try {
        await new OpenAIChatClient(null, http).StreamCompletionAsync(request, null);
        throw new InvalidOperationException("HTTP failure was accepted.");
    }
    catch (CompletionFailureException error) {
        Require(error.Failure == new CompletionFailureInfo(CompletionFailureKind.Http, 429,
            "rate_limit_exceeded", TimeSpan.FromSeconds(7)), "HTTP failure facts were lost.");
    }
    Require(handler.Calls == 1, "Library retried an HTTP failure.");
}

using (var handler = new ControlledHandler(Frame("partial", null)))
using (var http = CreateHttp(handler)) {
    try {
        await new OpenAIChatClient(null, http, OpenAIChatDialects.SgLangCompatible)
            .StreamCompletionAsync(request, null);
        throw new InvalidOperationException("EOF before terminal was accepted.");
    }
    catch (CompletionStreamInterruptedException error) {
        Require(error.Failure.Kind == CompletionFailureKind.Transport, "EOF transport fact was lost.");
    }
    Require(handler.Calls == 1, "Uncertain outcome was retried.");
}

using (var handler = new ControlledHandler(null))
using (var http = CreateHttp(handler))
using (var caller = new CancellationTokenSource()) {
    Task<CompletionResult> pending = new OpenAIChatClient(null, http, OpenAIChatDialects.SgLangCompatible)
        .StreamCompletionAsync(request, null, caller.Token);
    await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    caller.Cancel();
    try {
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        throw new InvalidOperationException("Cancellation was ignored.");
    }
    catch (OperationCanceledException error) {
        Require(error.CancellationToken == caller.Token, "Caller cancellation token was lost.");
    }
    Require(handler.Calls == 1, "Cancellation was retried.");
}

var tool = MethodToolWrapper.FromDelegate<EchoInput>(new EchoHost().EchoAsync);
var registry = new ToolRegistry([tool]);
Require(registry.AllDefinitions.Length == 1 && registry.AllDefinitions[0].Name == "smoke.echo",
    "Tool definition missing.");
Require(tool.Definition.InputSchema is ToolSchema.Object schema
    && schema.Properties.Length == 1 && schema.Properties[0].Name == "text"
    && schema.Properties[0].IsRequired
    && schema.Properties[0].Schema is ToolSchema.Value { ValueKind: ToolParamType.String },
    "Reflected tool schema lost its required string property.");
var session = registry.CreateSession();
var execution = await session.ExecuteAsync(new RawToolCall("smoke.echo", "call-1", "{\"text\":\"bound\"}"), default);
Require(execution.ExecuteResult.GetFlattenedText() == "bound", "Tool input binding/execution failed.");
var invalid = await session.ExecuteAsync(new RawToolCall("smoke.echo", "call-2", "{\"text\":17}"), default);
Require(invalid.ExecuteResult.Status != ToolExecutionStatus.Success, "Invalid tool argument accepted.");
var textTool = TextToolWrapper.FromDelegate(new EchoHost().EchoTextAsync, ToolTextFormat.Grammar("regex", "(?s).*"));
var textSession = new ToolRegistry([textTool]).CreateSession();
const string rawText = " \r\n\t*** Begin Patch\n+\"raw\"\n*** End Patch\n";
var textCall = RawToolCall.FromText("smoke.text", "text-1", rawText);
var textExecution = await textSession.ExecuteAsync(textCall, default);
Require(textExecution.ExecuteResult.GetFlattenedText() == rawText, "Raw text tool altered input.");
var saved = ActionMessageSerialization.Serialize(new ActionMessage([new ActionBlock.ToolCall(textCall)]));
Require(ActionMessageSerialization.Deserialize(saved).ToolCalls.Single() == textCall, "Text call persistence changed input kind or text.");
var textRequest = new CompletionRequest("package-smoke", new CompletionPromptPrefix("Echo.",
    CompletionOutputContract.ProviderDefault([textTool.Definition]), []), [new ObservationMessage("echo")]);
var customItem = new { type = "custom_tool_call", id = "item-1", call_id = "text-2", name = "smoke.text", input = rawText };
var customStream = "event: response.completed\ndata: " + JsonSerializer.Serialize(new {
    type = "response.completed", response = new { status = "completed", output = new[] { customItem } }
}) + "\n\n";
using (var handler = new ControlledHandler(customStream))
using (var http = CreateHttp(handler)) {
    var result = await new OpenAIResponsesClient(null, http).StreamCompletionAsync(textRequest, null);
    Require(result.Termination.Kind == CompletionTerminationKind.Completed, "Text completion failed.");
    var call = result.Message.ToolCalls.Single();
    Require(call.InputKind == ToolInputKind.Text && call.RawInput == rawText, "Responses text input lost.");
    using var wire = JsonDocument.Parse(handler.Body!);
    Require(wire.RootElement.GetProperty("tools")[0].GetProperty("type").GetString() == "custom", "Custom tool declaration lost.");
}
Console.WriteLine("Package smoke passed: Chat wire/status/failure facts/Retry-After/unknown usage/EOF/cancellation and JSON/text tool declaration/binding/execution/persistence/Responses.");

static HttpClient CreateHttp(HttpMessageHandler handler) => new(handler) {
    BaseAddress = new Uri("https://package-smoke.invalid/"), Timeout = Timeout.InfiniteTimeSpan
};
static string Frame(string content, string? finish) => "data: " + JsonSerializer.Serialize(new {
    choices = new[] { new { index = 0, delta = new { content }, finish_reason = finish } }
}) + "\n\n";
static void Require(bool condition, string message) {
    if (!condition) { throw new InvalidOperationException(message); }
}
static void CheckWire(ControlledHandler handler) {
    Require(handler.Calls == 1, "Unexpected HTTP call count.");
    using var json = JsonDocument.Parse(handler.Body!);
    Require(json.RootElement.GetProperty("model").GetString() == "package-smoke", "Wrong model projection.");
    Require(json.RootElement.GetProperty("stream").GetBoolean(), "Streaming request missing.");
    Require(json.RootElement.GetProperty("messages").EnumerateArray().Any(message =>
        message.GetProperty("role").GetString() == "user" && message.GetProperty("content").GetString() == "hello"),
        "User message projection missing.");
}

sealed class ControlledHandler(string? response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler {
    public int Calls { get; private set; }
    public string? Body { get; private set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        Calls++;
        Body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Entered.TrySetResult();
        if (response is null) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        var result = new HttpResponseMessage(status) {
            Content = new StringContent(response!, Encoding.UTF8, "text/event-stream")
        };
        if (status == HttpStatusCode.TooManyRequests) { result.Headers.RetryAfter = new(TimeSpan.FromSeconds(7)); }
        return result;
    }
}

public sealed record EchoInput([property: JsonPropertyName("text"), Required, Description("Text to echo.")] string Text);
public sealed class EchoHost {
    [Tool("smoke.text", "Echo raw text.")]
    public ValueTask<ToolExecuteResult> EchoTextAsync(string input, ToolExecutionContext context, CancellationToken ct) =>
        ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Success, input));
    [Tool("smoke.echo", "Echo a bound string without side effects.")]
    public ValueTask<ToolExecuteResult> EchoAsync(EchoInput input, ToolExecutionContext context, CancellationToken ct) =>
        ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Success, input.Text));
}
