using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesCustomToolTests {
    private static readonly CompletionDescriptor Invocation = new("openai", "openai-responses-v2", "test");

    [Fact]
    public async Task PublicClient_CustomToolHttpRoundTrip() {
        const string input = "\n\tconst a = `x`;\n";
        using var handler = new CustomToolHandler(Terminal(input));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        var client = new OpenAIResponsesClient(apiKey: null, httpClient: http);
        var request = new CompletionRequest("test", new CompletionPromptPrefix("",
            CompletionOutputContract.ProviderDefault([ToolDefinition.FromText("apply_patch", "Patch")]),
            [new ObservationMessage("patch")]), []);
        var first = await client.StreamCompletionAsync(request, null, CancellationToken.None);
        Assert.Equal(input, Assert.IsType<ActionBlock.ToolCall>(Assert.Single(first.Message.Blocks)).Call.RawInput);
        var followup = new CompletionRequest("test", new CompletionPromptPrefix("",
            CompletionOutputContract.ProviderDefault([]), [first.Message,
                new ToolResultsMessage(null, [ToolResult.FromText("apply_patch", "call_1", ToolExecutionStatus.Success, "ok")])]), []);
        _ = await client.StreamCompletionAsync(followup, null, CancellationToken.None);
        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("custom", sent.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        using var replay = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal(input, replay.RootElement.GetProperty("input")[0].GetProperty("input").GetString());
        Assert.Equal("custom_tool_call_output", replay.RootElement.GetProperty("input")[1].GetProperty("type").GetString());
    }

    private sealed class CustomToolHandler(string firstEvent) : HttpMessageHandler {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("data: " + (Bodies.Count == 1 ? firstEvent : "{\"type\":\"response.completed\"}") + "\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("lark")]
    [InlineData("regex")]
    public void Projection_UsesCustomFormatAndNativeNamedChoice(string? syntax) {
        var definition = ToolDefinition.FromText("apply_patch", "Apply text", syntax is null
            ? null : ToolTextFormat.Grammar(syntax, "grammar-body"));
        var request = new CompletionRequest("test", new CompletionPromptPrefix("",
            new CompletionOutputContract([definition], CompletionToolChoice.RequiredNamed("apply_patch")), []), []);
        var projected = OpenAIResponsesMessageConverter.ConvertToApiRequest(request);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(projected));
        var tool = json.RootElement.GetProperty("tools")[0];
        Assert.Equal("custom", tool.GetProperty("type").GetString());
        Assert.False(tool.TryGetProperty("parameters", out _));
        Assert.False(tool.TryGetProperty("strict", out _));
        Assert.Equal(syntax is null ? "text" : "grammar", tool.GetProperty("format").GetProperty("type").GetString());
        if (syntax is not null) {
            Assert.Equal(syntax, tool.GetProperty("format").GetProperty("syntax").GetString());
            Assert.Equal("grammar-body", tool.GetProperty("format").GetProperty("definition").GetString());
        }
        Assert.Equal("custom", json.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
        var codexProjection = OpenAIResponsesMessageConverter.ConvertToApiRequest(request,
            supportsNativeRequiredNamedToolChoice: false);
        Assert.Equal("required", codexProjection.ToolChoice);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("*** Begin Patch\n*** Add File: x\n+` ${x} \\\" 中\n*** End Patch\n")]
    public void StreamAndReplay_PreservesRawInputAndCallKindWithoutCurrentDefinition(string input) {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(Invocation);
        parser.ParseEvent(Added, aggregator);
        int split = input.Length / 2;
        parser.ParseEvent(JsonSerializer.Serialize(new { type = "response.custom_tool_call_input.delta", item_id = "ct_1", delta = input[..split] }), aggregator);
        parser.ParseEvent(JsonSerializer.Serialize(new { type = "response.custom_tool_call_input.delta", item_id = "ct_1", delta = input[split..] }), aggregator);
        parser.ParseEvent(JsonSerializer.Serialize(new { type = "response.custom_tool_call_input.done", item_id = "ct_1", input }), aggregator);
        parser.ParseEvent(ItemDone(input), aggregator);
        parser.ParseEvent(Terminal(input), aggregator);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
        var call = Assert.IsType<ActionBlock.ToolCall>(Assert.Single(result.Message.Blocks)).Call;
        Assert.Equal(ToolInputKind.Text, call.InputKind);
        Assert.Equal(input, call.RawInput);
        var replay = new CompletionRequest("test", new CompletionPromptPrefix("",
            CompletionOutputContract.ProviderDefault([]), [result.Message,
                new ToolResultsMessage(null, [ToolResult.FromText("apply_patch", "call_1", ToolExecutionStatus.Success, "ok")])]), []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(OpenAIResponsesMessageConverter.ConvertToApiRequest(replay)));
        var items = json.RootElement.GetProperty("input");
        Assert.Equal("custom_tool_call", items[0].GetProperty("type").GetString());
        Assert.Equal(input, items[0].GetProperty("input").GetString());
        Assert.Equal("custom_tool_call_output", items[1].GetProperty("type").GetString());
        Assert.Equal("call_1", items[1].GetProperty("call_id").GetString());
    }

    [Theory]
    [InlineData("item")]
    [InlineData("terminal")]
    [InlineData("input-without-metadata")]
    public void MissingStreamEvents_FinalSnapshotSuppliesCustomCall(string source) {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(Invocation);
        const string input = " \ntext\n ";
        if (source == "input-without-metadata") {
            parser.ParseEvent(JsonSerializer.Serialize(new { type = "response.custom_tool_call_input.done", item_id = "ct_1", input }), aggregator);
        }
        if (source == "item") { parser.ParseEvent(ItemDone(input), aggregator); }
        parser.ParseEvent(Terminal(input), aggregator);
        Assert.Equal(input, Assert.IsType<ActionBlock.ToolCall>(Assert.Single(aggregator.Build().Message.Blocks)).Call.RawInput);
    }

    [Theory]
    [InlineData("delta-after-done")]
    [InlineData("changed-final")]
    [InlineData("wrong-prefix")]
    [InlineData("changed-kind")]
    public void InconsistentCustomEvents_AreRejected(string scenario) {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(Invocation);
        parser.ParseEvent(Added, aggregator);
        parser.ParseEvent("""{"type":"response.custom_tool_call_input.delta","item_id":"ct_1","delta":"prefix"}""", aggregator);
        if (scenario != "wrong-prefix") { parser.ParseEvent(ItemDone("prefix"), aggregator); }
        string inconsistent = scenario switch {
            "delta-after-done" => """{"type":"response.custom_tool_call_input.delta","item_id":"ct_1","delta":"extra"}""",
            "changed-kind" => """{"type":"response.output_item.done","item":{"id":"ct_1","type":"function_call","name":"apply_patch","call_id":"call_1","arguments":"{}"}}""",
            _ => ItemDone("different")
        };
        Assert.Throws<InvalidDataException>(() => parser.ParseEvent(inconsistent, aggregator));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("in_progress")]
    public void TerminalPartialItem_IsNotAnExecutableToolCall(string status) {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(Invocation);
        parser.ParseEvent(JsonSerializer.Serialize(new {
            type = "response.incomplete", response = new { output = new[] { new {
                type = "custom_tool_call", id = "ct_1", call_id = "call_1", name = "apply_patch", input = "partial", status
            } } }
        }), aggregator);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Incomplete, result.Termination.Kind);
        Assert.DoesNotContain(result.Message.Blocks, block => block is ActionBlock.ToolCall);
    }

    [Fact]
    public void MixedHistory_UsesEachHistoricalInputKindAndOrdersResultsByCall() {
        var request = new CompletionRequest("test", new CompletionPromptPrefix("",
            CompletionOutputContract.ProviderDefault([]), [new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("read_file", "json_call", "{\"path\":\"x\"}")),
                new ActionBlock.ToolCall(RawToolCall.FromText("apply_patch", "text_call", "\npatch\n"))
            ]), new ToolResultsMessage(null, [
                ToolResult.FromText("apply_patch", "text_call", ToolExecutionStatus.Success, "patched"),
                ToolResult.FromText("read_file", "json_call", ToolExecutionStatus.Success, "contents")
            ])]), []);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(OpenAIResponsesMessageConverter.ConvertToApiRequest(request)));
        var items = json.RootElement.GetProperty("input");
        Assert.Equal("function_call", items[0].GetProperty("type").GetString());
        Assert.Equal("custom_tool_call", items[1].GetProperty("type").GetString());
        Assert.Equal("function_call_output", items[2].GetProperty("type").GetString());
        Assert.Equal("json_call", items[2].GetProperty("call_id").GetString());
        Assert.Equal("custom_tool_call_output", items[3].GetProperty("type").GetString());
        Assert.Equal("text_call", items[3].GetProperty("call_id").GetString());
    }

    [Fact]
    public void UnfinishedInput_IsNotAnExecutableToolCall() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(Invocation);
        parser.ParseEvent(Added, aggregator);
        parser.ParseEvent("""{"type":"response.custom_tool_call_input.delta","item_id":"ct_1","delta":"partial"}""", aggregator);
        parser.ParseEvent("""{"type":"response.completed"}""", aggregator);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Incomplete, result.Termination.Kind);
        Assert.DoesNotContain(result.Message.Blocks, block => block is ActionBlock.ToolCall);
    }

    private const string Added = """{"type":"response.output_item.added","item":{"id":"ct_1","type":"custom_tool_call","name":"apply_patch","call_id":"call_1","input":""}}""";
    private static object Item(string input) => new { id = "ct_1", type = "custom_tool_call", name = "apply_patch", call_id = "call_1", input, status = "completed" };
    private static string ItemDone(string input) => JsonSerializer.Serialize(new { type = "response.output_item.done", item = Item(input) });
    private static string Terminal(string input) => JsonSerializer.Serialize(new { type = "response.completed", response = new { output = new[] { Item(input) } } });
}
