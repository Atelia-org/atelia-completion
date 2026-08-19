using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAIChatClientTests {
    [Fact]
    public async Task StreamCompletionAsync_ParsesContentWithoutRequestingUsage() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(
                HttpStatusCode.OK
            ) {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}],"usage":null}

                    data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.SgLangCompatible);
        var request = CreateRequest();

        var aggregated = await client.StreamCompletionAsync(request, null, CancellationToken.None);

        var requestBody = Assert.Single(handler.RequestBodies);
        Assert.DoesNotContain("\"stream_options\"", requestBody, StringComparison.Ordinal);
        Assert.Equal("text/event-stream", Assert.Single(handler.RequestAcceptHeaders));
        Assert.Equal("hello", aggregated.Message.GetFlattenedText());
    }

    [Fact]
    public async Task StrictSurface_RequestsAndCapturesTerminalUsageSnapshot() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":null}

                data: {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":80,"cache_write_tokens":12}}}

                data: [DONE]

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(
            null,
            httpClient,
            OpenAIChatDialects.Strict
        );

        CompletionResult result = await client.StreamCompletionAsync(
            CreateRequest(),
            new CompletionInvocationOptions {
                PromptCacheReuseHint = PromptCacheReuseHint.ReuseExpectedSoon
            },
            observer: null,
            CancellationToken.None
        );

        using JsonDocument request = JsonDocument.Parse(
            Assert.Single(handler.RequestBodies)
        );
        Assert.True(
            request.RootElement.GetProperty("stream_options")
                .GetProperty("include_usage")
                .GetBoolean()
        );
        Assert.Equal(8, result.Usage.UncachedInputTokens);
        Assert.Equal(12, result.Usage.CacheCreationInputTokens);
        Assert.Equal(80, result.Usage.CacheReadInputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal(
            PromptCacheRequestStatus.Requested,
            result.Usage.PromptCache.RequestStatus
        );
        Assert.Equal(
            PromptCacheSupportStatus.Unknown,
            result.Usage.PromptCache.SupportStatus
        );
        Assert.Equal(
            PromptCacheObservationStatus.Complete,
            result.Usage.PromptCache.ObservationStatus
        );
    }

    [Fact]
    public async Task DeepSeekV4Surface_RequestsAndMapsTerminalUsageSnapshot() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":null}

                data: {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5,"prompt_cache_hit_tokens":80,"prompt_cache_miss_tokens":20}}

                data: [DONE]

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new DeepSeekV4ChatClient(null, httpClient);

        CompletionResult result = await client.StreamCompletionAsync(
            CreateRequest(),
            new CompletionInvocationOptions {
                PromptCacheReuseHint = PromptCacheReuseHint.ReuseExpectedSoon
            },
            observer: null,
            CancellationToken.None
        );

        using JsonDocument request = JsonDocument.Parse(
            Assert.Single(handler.RequestBodies)
        );
        Assert.True(
            request.RootElement.GetProperty("stream_options")
                .GetProperty("include_usage")
                .GetBoolean()
        );
        Assert.Equal(20, result.Usage.UncachedInputTokens);
        Assert.Null(result.Usage.CacheCreationInputTokens);
        Assert.Equal(80, result.Usage.CacheReadInputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal(
            PromptCacheRequestStatus.Requested,
            result.Usage.PromptCache.RequestStatus
        );
        Assert.Equal(
            PromptCacheSupportStatus.Unknown,
            result.Usage.PromptCache.SupportStatus
        );
        Assert.Equal(
            PromptCacheObservationStatus.Partial,
            result.Usage.PromptCache.ObservationStatus
        );
        Assert.Equal(
            "implicit-best-effort",
            result.Usage.PromptCache.ProviderDiagnostics!["mapping"]
        );
        Assert.Equal(
            "true",
            result.Usage.PromptCache.ProviderDiagnostics!["streamUsageRequested"]
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_IncludesConfiguredExtraBodyFieldsAtRequestRoot() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":null}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient: httpClient,
            dialect: OpenAIChatDialects.QwenSgLang,
            options: new OpenAIChatClientOptions {
                ReasoningEffort = CompletionReasoningEffort.Disabled
            }
        );

        await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        var requestBody = Assert.Single(handler.RequestBodies);
        using var document = JsonDocument.Parse(requestBody);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("extra_body", out _));
        Assert.True(root.TryGetProperty("chat_template_kwargs", out var kwargs));
        Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, "none")]
    [InlineData(CompletionReasoningEffort.Low, "low")]
    [InlineData(CompletionReasoningEffort.Medium, "medium")]
    [InlineData(CompletionReasoningEffort.High, "high")]
    [InlineData(CompletionReasoningEffort.Max, "xhigh")]
    public async Task StreamCompletionAsync_MapsStrictOpenAIReasoningEffort(
        CompletionReasoningEffort effort,
        string expectedWireValue
    ) {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

                data: [DONE]

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient,
            OpenAIChatDialects.Strict,
            new OpenAIChatClientOptions { ReasoningEffort = effort }
        );

        await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(expectedWireValue, document.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, false)]
    [InlineData(CompletionReasoningEffort.Low, true)]
    [InlineData(CompletionReasoningEffort.Max, true)]
    public async Task StreamCompletionAsync_MapsQwenReasoningToThinkingSwitch(
        CompletionReasoningEffort effort,
        bool expectedEnabled
    ) {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

                data: [DONE]

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient,
            OpenAIChatDialects.QwenSgLang,
            new OpenAIChatClientOptions { ReasoningEffort = effort }
        );

        await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(
            expectedEnabled,
            document.RootElement
                .GetProperty("chat_template_kwargs")
                .GetProperty("enable_thinking")
                .GetBoolean()
        );
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, null, "disabled")]
    [InlineData(CompletionReasoningEffort.Low, "high", "enabled")]
    [InlineData(CompletionReasoningEffort.High, "high", "enabled")]
    [InlineData(CompletionReasoningEffort.Max, "max", "enabled")]
    public async Task StreamCompletionAsync_MapsDeepSeekV4ReasoningEffort(
        CompletionReasoningEffort effort,
        string? expectedWireValue,
        string expectedThinkingType
    ) {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

                data: [DONE]

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient,
            OpenAIChatDialects.DeepSeekV4,
            new OpenAIChatClientOptions { ReasoningEffort = effort }
        );

        await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = document.RootElement;
        Assert.Equal(
            expectedThinkingType,
            root.GetProperty("thinking").GetProperty("type").GetString()
        );
        if (expectedWireValue is null) {
            Assert.False(root.TryGetProperty("reasoning_effort", out _));
        }
        else {
            Assert.Equal(expectedWireValue, root.GetProperty("reasoning_effort").GetString());
        }
    }

    [Theory]
    [InlineData("model")]
    [InlineData("reasoning_effort")]
    [InlineData("thinking")]
    public async Task StreamCompletionAsync_ThrowsWhenExtraBodyCollidesWithReservedFields(
        string fieldName
    ) {
        using var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient: httpClient,
            dialect: OpenAIChatDialects.Strict,
            options: new OpenAIChatClientOptions {
                ExtraBody = new JsonObject {
                    [fieldName] = "should-not-override"
                }
            }
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Contains("collides with a reserved request property", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task StreamCompletionAsync_QwenRejectsDuplicateThinkingControlSource() {
        using var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient,
            OpenAIChatDialects.QwenSgLang,
            new OpenAIChatClientOptions {
                ReasoningEffort = CompletionReasoningEffort.High,
                ExtraBody = new JsonObject {
                    ["chat_template_kwargs"] = new JsonObject {
                        ["enable_thinking"] = false
                    }
                }
            }
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Contains("conflicts", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestBodies);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StreamCompletionAsync_RejectsExtraBodyChoiceCount(int choiceCount) {
        using var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient,
            OpenAIChatDialects.Strict,
            new OpenAIChatClientOptions {
                ExtraBody = new JsonObject {
                    ["n"] = choiceCount
                }
            }
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Contains("'n'", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public void Constructor_RequiresPreconfiguredHttpClientBaseAddress() {
        using var handler = new SequenceHttpMessageHandler();
        using var httpClient = new HttpClient(handler);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.Strict)
        );

        Assert.Contains("HttpClient.BaseAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_UsesPreconfiguredHttpClientBaseAddress() {
        using var handler = new SequenceHttpMessageHandler();
        var preconfigured = new Uri("http://localhost:9000/");
        using var httpClient = new HttpClient(handler) {
            BaseAddress = preconfigured
        };

        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.Strict);

        Assert.NotNull(client);
        Assert.Equal(preconfigured, httpClient.BaseAddress);
        Assert.Equal(preconfigured.Host, client.Name);
    }

    [Fact]
    public void Constructor_RejectsBaseAddressWithoutTrailingSlash() {
        using var handler = new SequenceHttpMessageHandler();
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:9000/openai")
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.Strict)
        );

        Assert.Contains("end with '/'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_UsesNormalizedBaseAddressFromTransportFactory() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":null}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = CompletionHttpTransportFactory.CreateLiveClient(
            new Uri("http://localhost:8000/prefix"),
            handler
        );
        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.Strict);

        var aggregated = await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        Assert.Equal("ok", aggregated.Message.GetFlattenedText());
        Assert.Equal(new Uri("http://localhost:8000/prefix/"), httpClient.BaseAddress);
        Assert.Equal("http://localhost:8000/prefix/v1/chat/completions", Assert.Single(handler.RequestUris));
    }

    [Fact]
    public async Task StreamCompletionAsync_EarlyStop_DoesNotFlushIncompleteToolCalls() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_123","index":0,"type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Par"}}]},"finish_reason":null}],"usage":null}

                    data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"type":"function","function":{"arguments":"is\"}"}}]},"finish_reason":"tool_calls"}],"usage":null}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIChatClient(apiKey: null, httpClient: httpClient, dialect: OpenAIChatDialects.SgLangCompatible);
        var observer = new CompletionStreamObserver { ShouldStop = true };

        var aggregated = await client.StreamCompletionAsync(CreateRequest(), observer, CancellationToken.None);

        Assert.DoesNotContain(aggregated.Message.Blocks, block => block.Kind == ActionBlockKind.ToolCall);
        var text = Assert.Single(aggregated.Message.Blocks);
        Assert.Equal(string.Empty, Assert.IsType<ActionBlock.Text>(text).Content);
    }

    [Fact]
    public async Task StreamCompletionAsync_NonSuccessStatus_IncludesResponseBodySnippetInException() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent(
                    """
                    {"error":{"message":"bad input","type":"invalid_request_error"}}
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new OpenAIChatClient(
            apiKey: null,
            httpClient: httpClient,
            dialect: OpenAIChatDialects.SgLangCompatible
        );

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("bad input", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_DeepSeekClientReplaysReasoningContentIntoRequestBody() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}],"usage":null}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                )
            }
        );

        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = new DeepSeekV4ChatClient(apiKey: null, httpClient: httpClient);
        var request = new CompletionRequest(
            "deepseek-v4",
            new CompletionPromptPrefix(
                string.Empty,
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new IHistoryMessage[] {
                new ActionMessage(
                    new ActionBlock[] {
                        new OpenAIChatReasoningBlock(
                            "Need continuity.",
                            new CompletionDescriptor("localhost", "openai-chat-v1", "deepseek-v4")
                        ),
                        new ActionBlock.Text("hello")
                    }
                )
            }
            ),
            tailMessages: []
        );

        await client.StreamCompletionAsync(request, null, CancellationToken.None);

        var requestBody = Assert.Single(handler.RequestBodies);
        using var document = JsonDocument.Parse(requestBody);
        var messages = document.RootElement.GetProperty("messages");
        var assistantMessage = messages.EnumerateArray().Single(message => message.GetProperty("role").GetString() == "assistant");
        Assert.Equal("Need continuity.", assistantMessage.GetProperty("reasoning_content").GetString());
        Assert.Equal("hello", assistantMessage.GetProperty("content").GetString());
    }

    [Fact]
    public async Task StreamCompletionAsync_EofBeforeFinishReasonIsUncertainInterruption() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"content":"partial"},"finish_reason":null}]}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, httpClient, OpenAIChatDialects.Strict);

        var exception = await Assert.ThrowsAsync<CompletionStreamInterruptedException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Equal("OpenAI chat/completions", exception.StreamDisplayName);
    }

    [Fact]
    public async Task StreamCompletionAsync_DoneBeforeFinishReasonIsUncertainInterruption() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"content":"partial"},"finish_reason":null}]}

                data: [DONE]

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, httpClient, OpenAIChatDialects.Strict);

        await Assert.ThrowsAsync<CompletionStreamInterruptedException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_FinishReasonReturnsWithoutReadingLaterFrames() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"content":"done"},"finish_reason":"stop"}]}

                data: {not-json}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(
            null,
            httpClient,
            OpenAIChatDialects.SgLangCompatible
        );

        var result = await client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None);

        Assert.Equal("done", result.Message.GetFlattenedText());
        Assert.Equal(CompletionTerminationKind.Completed, result.Termination.Kind);
    }

    [Fact]
    public async Task StreamCompletionAsync_InterruptionClosesObserverThinkingLifecycle() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"reasoning_content":"still thinking"},"finish_reason":null}]}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, httpClient, OpenAIChatDialects.DeepSeekV4);
        var observer = new CompletionStreamObserver();
        var thinkingBeginCount = 0;
        var thinkingEndCount = 0;
        observer.ReceivedThinkingBegin += () => thinkingBeginCount++;
        observer.ReceivedThinkingEnd += () => thinkingEndCount++;

        await Assert.ThrowsAsync<CompletionStreamInterruptedException>(
            () => client.StreamCompletionAsync(CreateRequest(), observer, CancellationToken.None)
        );

        Assert.Equal(1, thinkingBeginCount);
        Assert.Equal(1, thinkingEndCount);
    }

    [Fact]
    public async Task StreamCompletionAsync_CleanupFailureDoesNotMaskInterruption() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"reasoning_content":"still thinking"},"finish_reason":null}]}

                """
                + "\n"
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, httpClient, OpenAIChatDialects.DeepSeekV4);
        var observer = new CompletionStreamObserver();
        observer.ReceivedThinkingEnd += () => throw new InvalidOperationException(
            "scripted observer cleanup failure"
        );

        var exception = await Assert.ThrowsAsync<CompletionStreamInterruptedException>(
            () => client.StreamCompletionAsync(CreateRequest(), observer, CancellationToken.None)
        );

        Assert.Equal("OpenAI chat/completions", exception.StreamDisplayName);
    }

    [Fact]
    public async Task StreamCompletionAsync_CleanupFailureDoesNotMaskCallerCancellationToken() {
        var handler = new SequenceHttpMessageHandler(
            EventStreamResponse(
                """
                data: {"choices":[{"index":0,"delta":{"reasoning_content":"still thinking"},"finish_reason":null}]}

                data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                """
            )
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, httpClient, OpenAIChatDialects.DeepSeekV4);
        using var caller = new CancellationTokenSource();
        var observer = new CompletionStreamObserver();
        observer.ReceivedReasoningDelta += _ => caller.Cancel();
        observer.ReceivedThinkingEnd += () => throw new InvalidOperationException(
            "scripted observer cleanup failure"
        );

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.StreamCompletionAsync(CreateRequest(), observer, caller.Token)
        );

        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task StreamCompletionAsync_SuccessWithNonEventStreamMediaTypeIsProtocolError() {
        var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            }
        );
        using var httpClient = CreateHttpClient(handler);
        var client = new OpenAIChatClient(null, httpClient, OpenAIChatDialects.Strict);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.StreamCompletionAsync(CreateRequest(), null, CancellationToken.None)
        );

        Assert.Contains("text/event-stream", exception.Message, StringComparison.Ordinal);
        Assert.Contains("application/json", exception.Message, StringComparison.Ordinal);
    }

    private static CompletionRequest CreateRequest() {
        return new CompletionRequest(
            "gpt-4.1",
            new CompletionPromptPrefix(
                "system",
                CompletionOutputContract.ProviderDefault(ImmutableArray<ToolDefinition>.Empty),
                new[] { new ObservationMessage("hello") }
            ),
            tailMessages: []
        );
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) {
        return new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };
    }

    private static HttpResponseMessage EventStreamResponse(string body) {
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHttpMessageHandler(params HttpResponseMessage[] responses) {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<string> RequestBodies { get; } = new();
        public List<string?> RequestUris { get; } = new();
        public List<string> RequestAcceptHeaders { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            RequestUris.Add(request.RequestUri?.ToString());
            RequestAcceptHeaders.Add(request.Headers.Accept.ToString());

            if (request.Content is not null) {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            else {
                RequestBodies.Add(string.Empty);
            }

            return _responses.Dequeue();
        }
    }
}
