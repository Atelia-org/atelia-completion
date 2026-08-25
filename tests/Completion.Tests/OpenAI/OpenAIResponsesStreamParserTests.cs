using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIResponsesStreamParserTests {
    private static CompletionDescriptor DummyInvocation => new("openai", "openai-responses-v2", "gpt-5");

    [Fact]
    public void TerminalUsage_ProjectsIndependentReadAndWriteCounters() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.completed","response":{"usage":{"input_tokens":120,"output_tokens":9,"input_tokens_details":{"cached_tokens":70,"cache_write_tokens":30}}}}
            """,
            aggregator
        );

        CompletionUsage usage = aggregator.Build().Usage;
        Assert.Equal(20, usage.UncachedInputTokens);
        Assert.Equal(30, usage.CacheCreationInputTokens);
        Assert.Equal(70, usage.CacheReadInputTokens);
        Assert.Equal(9, usage.OutputTokens);
        Assert.Equal(
            PromptCacheObservationStatus.Complete,
            usage.PromptCache.ObservationStatus
        );
    }

    [Fact]
    public void TerminalUsage_RejectsOverlappingCountersAboveTotalInput() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        Assert.Throws<InvalidDataException>(() => parser.ParseEvent(
            """
            {"type":"response.completed","response":{"usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":8,"cache_write_tokens":5}}}}
            """,
            aggregator
        ));
    }

    [Fact]
    public void ParseEvent_AggregatesReasoningToolCallAndTextFromMinimalEventSet() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"Need tool."}],"encrypted_content":"abc"}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"city\":\"Par"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"city\":\"Paris\"}","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather"}}
            """,
            aggregator
        );
        parser.ParseEvent("""{"type":"response.output_text.delta","delta":"Sunny."}""", aggregator);
        parser.ParseEvent("""{"type":"response.completed"}""", aggregator);

        var result = aggregator.Build();

        Assert.Collection(
            result.Message.Blocks,
            block => {
                var reasoning = Assert.IsType<OpenAIResponsesReasoningBlock>(block);
                Assert.Equal("Need tool.", reasoning.PlainText);
                Assert.Contains("\"encrypted_content\":\"abc\"", reasoning.RawItemJson, StringComparison.Ordinal);
            },
            block => {
                var toolCall = Assert.IsType<ActionBlock.ToolCall>(block).Call;
                Assert.Equal("call_123", toolCall.ToolCallId);
                Assert.Equal("get_weather", toolCall.ToolName);
                Assert.Equal("{\"city\":\"Paris\"}", toolCall.RawArgumentsJson);
            },
            block => Assert.Equal("Sunny.", Assert.IsType<ActionBlock.Text>(block).Content)
        );
    }

    [Fact]
    public void ParseEvent_ForwardsReasoningSummaryDeltasAsPlaintext() {
        var parser = new OpenAIResponsesStreamParser();
        var observer = new CompletionStreamObserver();
        var deltas = new List<string>();
        observer.ReceivedReasoningDelta += deltas.Add;
        var aggregator = new CompletionAggregator(DummyInvocation, observer);

        parser.ParseEvent(
            """
            {"type":"response.output_item.added","item":{"id":"rs_1","type":"reasoning","summary":[]}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","delta":"Need "}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","delta":"tool."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"Need tool."}],"encrypted_content":"abc"}}
            """,
            aggregator
        );
        parser.ParseEvent("""{"type":"response.completed"}""", aggregator);

        Assert.Equal(["Need ", "tool."], deltas);
        var reasoning = Assert.IsType<OpenAIResponsesReasoningBlock>(
            Assert.Single(aggregator.Build().Message.Blocks)
        );
        Assert.Equal("Need tool.", reasoning.PlainText);
    }

    [Fact]
    public void ParseEvent_RejectsReasoningSummaryDeltaThatDiffersFromCompletedItem() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.output_item.added","item":{"id":"rs_1","type":"reasoning","summary":[]}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","delta":"streamed"}
            """,
            aggregator
        );

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """
                {"type":"response.output_item.done","item":{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"different"}]}}
                """,
                aggregator
            )
        );

        Assert.Contains("do not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_ReasoningWithoutReadableSummaryUsesNullPlainText() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"rs_1","type":"reasoning","encrypted_content":"abc"}}
            """,
            aggregator
        );

        var reasoning = Assert.IsType<OpenAIResponsesReasoningBlock>(
            Assert.Single(aggregator.Build().Message.Blocks)
        );
        Assert.Null(reasoning.PlainText);
    }

    [Fact]
    public void ParseEvent_OutputItemDoneFinalizesFunctionCallWithoutArgumentsDone() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"city\":\"Paris\"}"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}
            """,
            aggregator
        );

        var result = aggregator.Build();

        var toolCall = Assert.IsType<ActionBlock.ToolCall>(Assert.Single(result.Message.Blocks)).Call;
        Assert.Equal("call_123", toolCall.ToolCallId);
        Assert.Equal("get_weather", toolCall.ToolName);
        Assert.Equal("{\"city\":\"Paris\"}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public void ParseEvent_OutputItemDoneDoesNotDuplicateFunctionCallAfterArgumentsDone() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"city\":\"Par"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"city\":\"Paris\"}","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather"}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"fc_1","type":"function_call","call_id":"call_123","name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}
            """,
            aggregator
        );

        var result = aggregator.Build();

        var toolCall = Assert.IsType<ActionBlock.ToolCall>(Assert.Single(result.Message.Blocks)).Call;
        Assert.Equal("call_123", toolCall.ToolCallId);
        Assert.Equal("get_weather", toolCall.ToolName);
        Assert.Equal("{\"city\":\"Paris\"}", toolCall.RawArgumentsJson);
    }

    [Fact]
    public void ParseEvent_FailedIsSemanticTerminalAndAppendsError() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.failed","response":{"error":{"message":"stream failed"}}}
            """,
            aggregator
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal(["stream failed"], result.Errors);
    }

    [Fact]
    public void ParseEvent_OfficialErrorEventIsSemanticTerminalAndAppendsError() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"error","code":"invalid_request_error","message":"bad input","param":null,"sequence_number":7}
            """,
            aggregator,
            "error"
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal(["bad input"], result.Errors);
    }

    [Fact]
    public void ParseEvent_ResponseIncompletePreservesProviderReason() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.output_item.added","item":{"id":"rs_pending","type":"reasoning"}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.function_call_arguments.delta","item_id":"fc_pending","delta":"{\"city\":"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.incomplete","response":{"incomplete_details":{"reason":"max_output_tokens"}}}
            """,
            aggregator,
            "response.incomplete"
        );

        Assert.True(parser.TerminalEventObserved);
        var result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Incomplete, result.Termination.Kind);
        Assert.Equal("max_output_tokens", result.Termination.ProviderReason);
        Assert.Contains("max_output_tokens", result.Termination.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_RefusalDeltaAndDoneWaitForResponseTerminalThenMarkIncomplete() {
        var parser = new OpenAIResponsesStreamParser();
        var observer = new CompletionStreamObserver();
        var deltas = new List<string>();
        observer.ReceivedTextDelta += deltas.Add;
        var aggregator = new CompletionAggregator(DummyInvocation, observer);

        parser.ParseEvent(
            """
            {"type":"response.refusal.delta","item_id":"msg_1","content_index":0,"delta":"I can"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"I cannot help."}
            """,
            aggregator
        );

        Assert.False(parser.TerminalEventObserved);

        parser.ParseEvent(
            """{"type":"response.completed"}""",
            aggregator
        );

        Assert.True(parser.TerminalEventObserved);
        CompletionResult result = aggregator.Build();
        AssertRefusalTermination(result, "OpenAI Responses returned a typed refusal.");
        Assert.Equal("I cannot help.", result.Message.GetFlattenedText());
        Assert.Equal(["I can", "not help."], deltas);
        Assert.Null(result.Errors);
    }

    [Fact]
    public void ParseEvent_RefusalFinalWitnessesDeduplicateAndOnlyAppendMissingSuffix() {
        var parser = new OpenAIResponsesStreamParser();
        var observer = new CompletionStreamObserver();
        var deltas = new List<string>();
        observer.ReceivedTextDelta += deltas.Add;
        var aggregator = new CompletionAggregator(DummyInvocation, observer);

        parser.ParseEvent(
            """
            {"type":"response.refusal.delta","item_id":"msg_1","content_index":0,"delta":"No"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"No thanks"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"msg_1","type":"message","content":[{"type":"refusal","refusal":"No thanks"}]}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.completed","response":{"output":[{"id":"msg_1","type":"message","content":[{"type":"refusal","refusal":"No thanks"}]}]}}
            """,
            aggregator
        );

        CompletionResult result = aggregator.Build();
        AssertRefusalTermination(result, "OpenAI Responses returned a typed refusal.");
        Assert.Equal("No thanks", result.Message.GetFlattenedText());
        Assert.Equal(["No", " thanks"], deltas);
    }

    [Fact]
    public void ParseEvent_RefusalCoordinationSeparatesContentIndexesWithinOneItem() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.delta","item_id":"msg_1","content_index":0,"delta":"First."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"First."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.refusal.delta","item_id":"msg_1","content_index":1,"delta":"Second."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":1,"refusal":"Second."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"msg_1","type":"message","content":[{"type":"refusal","refusal":"First."},{"type":"refusal","refusal":"Second."}]}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """{"type":"response.completed"}""",
            aggregator
        );

        CompletionResult result = aggregator.Build();
        AssertRefusalTermination(result, "OpenAI Responses returned a typed refusal.");
        Assert.Equal("First.Second.", result.Message.GetFlattenedText());
    }

    [Fact]
    public void ParseEvent_CompletedEarlierContentCanRepeatBeforeFinalizingActiveContent() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.delta","item_id":"msg_1","content_index":0,"delta":"First."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"First."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.refusal.delta","item_id":"msg_1","content_index":1,"delta":"Sec"}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"msg_1","type":"message","content":[{"type":"refusal","refusal":"First."},{"type":"refusal","refusal":"Second."}]}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """{"type":"response.completed"}""",
            aggregator
        );

        CompletionResult result = aggregator.Build();
        AssertRefusalTermination(result, "OpenAI Responses returned a typed refusal.");
        Assert.Equal("First.Second.", result.Message.GetFlattenedText());
    }

    [Fact]
    public void ParseEvent_InterleavedRefusalContentFailsClosedWithoutBodyInException() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.delta","item_id":"msg_1","content_index":0,"delta":"REFUSAL_FIRST_BODY_CANARY"}
            """,
            aggregator
        );

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """
                {"type":"response.refusal.delta","item_id":"msg_1","content_index":1,"delta":"REFUSAL_SECOND_BODY_CANARY"}
                """,
                aggregator
            )
        );

        Assert.False(parser.TerminalEventObserved);
        Assert.DoesNotContain("CANARY", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_RefusalFinalForDifferentActiveContentFailsClosedWithoutBodyInException() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.delta","item_id":"msg_1","content_index":0,"delta":"REFUSAL_ACTIVE_BODY_CANARY"}
            """,
            aggregator
        );

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """
                {"type":"response.refusal.done","item_id":"msg_1","content_index":1,"refusal":"REFUSAL_OTHER_BODY_CANARY"}
                """,
                aggregator
            )
        );

        Assert.False(parser.TerminalEventObserved);
        Assert.DoesNotContain("CANARY", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_CompletedFinalOutputRefusalFallbackMarksIncomplete() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.completed","response":{"output":[{"id":"msg_1","type":"message","content":[{"type":"refusal","refusal":"Cannot comply."}]}]}}
            """,
            aggregator
        );

        CompletionResult result = aggregator.Build();
        AssertRefusalTermination(result, "OpenAI Responses returned a typed refusal.");
        Assert.Equal("Cannot comply.", result.Message.GetFlattenedText());
    }

    [Fact]
    public void ParseEvent_OrdinaryTextThatSoundsLikeRefusalRemainsCompleted() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.output_text.delta","delta":"I refuse to guess from plain text."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """{"type":"response.completed"}""",
            aggregator
        );

        CompletionResult result = aggregator.Build();
        Assert.Equal(
            CompletionTerminationKind.Completed,
            result.Termination.Kind
        );
        Assert.Equal(
            "I refuse to guess from plain text.",
            result.Message.GetFlattenedText()
        );
    }

    [Fact]
    public void ParseEvent_MixedTextToolAndRefusalRemainsIncomplete() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"response.output_text.delta","delta":"Visible text."}""",
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"msg_1","type":"message","content":[{"type":"refusal","refusal":"Refused."}]}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """{"type":"response.completed"}""",
            aggregator
        );

        CompletionResult result = aggregator.Build();
        AssertRefusalTermination(result, "OpenAI Responses returned a typed refusal.");
        Assert.Contains(
            result.Message.Blocks,
            static block => block is ActionBlock.ToolCall
        );
        Assert.Equal("Visible text.Refused.", result.Message.GetFlattenedText());
    }

    [Fact]
    public void ParseEvent_RefusalFinalConflictFailsClosedWithoutBodyInException() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.delta","item_id":"msg_1","content_index":0,"delta":"REFUSAL_PREFIX_CANARY"}
            """,
            aggregator
        );

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """
                {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"REFUSAL_CONFLICT_CANARY"}
                """,
                aggregator
            )
        );

        Assert.False(parser.TerminalEventObserved);
        Assert.DoesNotContain("CANARY", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_ConflictingRepeatedRefusalFinalFailsClosedWithoutBodyInException() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"REFUSAL_FINAL_ONE_CANARY"}
            """,
            aggregator
        );

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """
                {"type":"response.output_item.done","item":{"id":"msg_1","type":"message","content":[{"type":"refusal","refusal":"REFUSAL_FINAL_TWO_CANARY"}]}}
                """,
                aggregator
            )
        );

        Assert.False(parser.TerminalEventObserved);
        Assert.DoesNotContain("CANARY", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_RefusalDeltaAfterFinalWitnessFailsClosedWithoutBodyInException() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"Final refusal."}
            """,
            aggregator
        );

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """
                {"type":"response.refusal.delta","item_id":"msg_1","content_index":0,"delta":"REFUSAL_LATE_CANARY"}
                """,
                aggregator
            )
        );

        Assert.False(parser.TerminalEventObserved);
        Assert.DoesNotContain("REFUSAL_LATE_CANARY", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEvent_ResponseFailedOverridesPriorRefusalEvidence() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"Transient refusal."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.failed","response":{"error":{"message":"provider failed"}}}
            """,
            aggregator
        );

        CompletionResult result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal("response.failed", result.Termination.ProviderReason);
        Assert.Equal(["provider failed"], result.Errors);
        Assert.Equal("Transient refusal.", result.Message.GetFlattenedText());
    }

    [Fact]
    public void ParseEvent_OfficialErrorOverridesPriorRefusalEvidence() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"Transient refusal."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"error","message":"provider failed"}
            """,
            aggregator
        );

        CompletionResult result = aggregator.Build();
        Assert.Equal(CompletionTerminationKind.Failed, result.Termination.Kind);
        Assert.Equal("error", result.Termination.ProviderReason);
        Assert.Equal(["provider failed"], result.Errors);
        Assert.Equal("Transient refusal.", result.Message.GetFlattenedText());
    }

    [Fact]
    public void ParseEvent_ResponseIncompleteWithRefusalUsesRefusalReason() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":"Cannot comply."}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.incomplete","response":{"incomplete_details":{"reason":"content_filter"}}}
            """,
            aggregator
        );

        CompletionResult result = aggregator.Build();
        AssertRefusalTermination(result, "OpenAI Responses returned a typed refusal.");
    }

    [Fact]
    public void ParseEvent_ResponseIncompleteFinalOnlyRefusalUsesSanitizedMetadataAndTransientText() {
        const string refusalBody = "INCOMPLETE_REFUSAL_BODY_ASCII_SECRET_CANARY";
        var parser = new OpenAIResponsesStreamParser(
            sanitizeProviderErrors: true
        );
        var aggregator = new CompletionAggregator(DummyInvocation);
        string terminalEvent = System.Text.Json.JsonSerializer.Serialize(new {
            type = "response.incomplete",
            response = new {
                incomplete_details = new { reason = "content_filter" },
                output = new[] {
                    new {
                        id = "msg_1",
                        type = "message",
                        content = new[] {
                            new { type = "refusal", refusal = refusalBody }
                        }
                    }
                }
            }
        });

        parser.ParseEvent(
            terminalEvent,
            aggregator
        );

        CompletionResult result = aggregator.Build();
        AssertRefusalTermination(
            result,
            "ChatGPT Codex returned a typed refusal."
        );
        Assert.Equal(refusalBody, result.Message.GetFlattenedText());
        Assert.Null(result.Errors);
        string terminationMetadata = string.Join(
            "\n",
            result.Termination.ProviderReason,
            result.Termination.Detail
        );
        Assert.DoesNotContain(
            refusalBody,
            terminationMetadata,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ParseEvent_UnknownWellFormedEventIsForwardCompatible() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """{"type":"response.future_progress","future_value":42}""",
            aggregator,
            "response.future_progress"
        );

        Assert.False(parser.TerminalEventObserved);
        parser.ParseEvent(
            """{"type":"response.completed"}""",
            aggregator,
            "response.completed"
        );

        Assert.True(parser.TerminalEventObserved);
        Assert.Equal(CompletionTerminationKind.Completed, aggregator.Build().Termination.Kind);
    }

    [Fact]
    public void ParseEvent_UnknownWellFormedOutputAndMessagePartTypesAreForwardCompatible() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        parser.ParseEvent(
            """
            {"type":"response.output_item.done","item":{"id":"msg_1","type":"message","content":[{"type":"future_content","value":42}]}}
            """,
            aggregator
        );
        parser.ParseEvent(
            """
            {"type":"response.completed","response":{"output":[{"type":"future_item","value":42},{"id":"msg_2","type":"message","content":[{"type":"future_content","value":43}]}]}}
            """,
            aggregator
        );

        Assert.True(parser.TerminalEventObserved);
        Assert.Equal(
            CompletionTerminationKind.Completed,
            aggregator.Build().Termination.Kind
        );
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"type\":7}")]
    public void ParseEvent_MalformedProviderShapeThrowsProtocolException(string json) {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        Assert.Throws<InvalidDataException>(() => parser.ParseEvent(json, aggregator));
        Assert.False(parser.TerminalEventObserved);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"bad input\"}}", "requires string field 'type'")]
    [InlineData("{\"type\":\"error\",\"error\":{\"message\":\"bad input\"}}", "does not match")]
    public void ParseEvent_RejectsNamedSseEventBeforeConsideringErrorShape(
        string json,
        string expectedMessage
    ) {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(json, aggregator, "response.completed")
        );

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.False(parser.TerminalEventObserved);
    }

    [Fact]
    public void ParseEvent_RejectsSseEventAndDataTypeMismatch() {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        var exception = Assert.Throws<InvalidDataException>(
            () => parser.ParseEvent(
                """{"type":"response.completed"}""",
                aggregator,
                "response.output_text.delta"
            )
        );

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.False(parser.TerminalEventObserved);
    }

    [Theory]
    [InlineData("{\"type\":\"response.output_text.delta\"}")]
    [InlineData("{\"type\":\"response.output_item.added\"}")]
    [InlineData("{\"type\":\"response.output_item.added\",\"item\":{\"type\":\"reasoning\"}}")]
    [InlineData("{\"type\":\"response.function_call_arguments.delta\",\"delta\":\"{}\"}")]
    [InlineData("{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\"}")]
    [InlineData("{\"type\":\"response.function_call_arguments.done\",\"arguments\":\"{}\"}")]
    [InlineData("{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc_1\"}")]
    [InlineData("{\"type\":\"response.output_item.done\"}")]
    [InlineData("{\"type\":\"response.refusal.delta\",\"content_index\":0,\"delta\":\"no\"}")]
    [InlineData("{\"type\":\"response.refusal.delta\",\"item_id\":\"msg_1\",\"delta\":\"no\"}")]
    [InlineData("{\"type\":\"response.refusal.delta\",\"item_id\":\"msg_1\",\"content_index\":-1,\"delta\":\"no\"}")]
    [InlineData("{\"type\":\"response.refusal.done\",\"content_index\":0,\"refusal\":\"no\"}")]
    [InlineData("{\"type\":\"response.refusal.done\",\"item_id\":\"msg_1\",\"refusal\":\"no\"}")]
    [InlineData("{\"type\":\"response.refusal.done\",\"item_id\":\"msg_1\",\"content_index\":0}")]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\"}}")]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"content\":{}}}")]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"content\":[7]}}")]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"content\":[{}]}}")]
    [InlineData("{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"content\":[{\"type\":7}]}}")]
    [InlineData("{\"type\":\"response.completed\",\"response\":{\"output\":null}}")]
    [InlineData("{\"type\":\"response.completed\",\"response\":{\"output\":{}}}")]
    [InlineData("{\"type\":\"response.completed\",\"response\":{\"output\":[7]}}")]
    [InlineData("{\"type\":\"response.completed\",\"response\":{\"output\":[{}]}}")]
    [InlineData("{\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":7}]}}")]
    [InlineData("{\"type\":\"response.completed\",\"response\":{\"output\":[{\"id\":\"msg_1\",\"type\":\"message\"}]}}")]
    public void ParseEvent_KnownEventMissingRequiredShapeFailsClosed(string json) {
        var parser = new OpenAIResponsesStreamParser();
        var aggregator = new CompletionAggregator(DummyInvocation);

        Assert.Throws<InvalidDataException>(() => parser.ParseEvent(json, aggregator));
        Assert.False(parser.TerminalEventObserved);
    }

    private static void AssertRefusalTermination(
        CompletionResult result,
        string expectedDetail
    ) {
        Assert.Equal(
            CompletionTerminationKind.Incomplete,
            result.Termination.Kind
        );
        Assert.Equal("response.refusal", result.Termination.ProviderReason);
        Assert.Equal(expectedDetail, result.Termination.Detail);
    }
}
