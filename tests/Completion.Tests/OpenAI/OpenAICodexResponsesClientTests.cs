using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.OpenAI.Tests;

public sealed class OpenAICodexResponsesClientTests {
    [Fact]
    public async Task StreamCompletionAsync_SendsPinnedRequestWithConfigurableOriginator() {
        CodexSubscriptionCredential credential = Credential(
            "ACCESS_CANARY",
            "ACCOUNT_CANARY",
            generation: 1
        );
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => CompletedResponse("OK"));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint,
            originator: "galatea-local"
        );

        CompletionResult result = await client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        );

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://chatgpt.com/backend-api/codex/responses",
            sent.Uri
        );
        Assert.Equal("Bearer ACCESS_CANARY", sent.Authorization);
        Assert.Equal("ACCOUNT_CANARY", sent.Header("ChatGPT-Account-ID"));
        Assert.Equal("galatea-local", sent.Header("originator"));
        Assert.StartsWith("Atelia/", sent.Header("User-Agent"));
        Assert.Equal("text/event-stream", sent.Accept);

        using JsonDocument body = JsonDocument.Parse(sent.Body);
        JsonElement root = body.RootElement;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(
            "reasoning.encrypted_content",
            root.GetProperty("include")[0].GetString()
        );
        Assert.DoesNotContain("ACCESS_CANARY", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("ACCOUNT_CANARY", sent.Body, StringComparison.Ordinal);
        Assert.Equal("OK", Assert.IsType<ActionBlock.Text>(Assert.Single(result.Message.Blocks)).Content);
    }

    [Fact]
    public async Task StreamCompletionAsync_MaxTokensFailsBeforeCredentialAndNetwork() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => CompletedResponse("unused"));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.StreamCompletionAsync(
                Request(maxTokens: 10),
                observer: null,
                CancellationToken.None
            )
        );

        Assert.Equal(0, provider.CallCount);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("recap_grid.control")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task StreamCompletionAsync_InvalidHistoricalToolCallNameIsKnownNoDispatchRejection(
        string toolName
    ) {
        CodexSubscriptionCredential credential = Credential(
            "token",
            "account",
            1
        );
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => CompletedResponse("unused"));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );
        CompletionRequest request = Request(sharedContext: [
            new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall(toolName, "call-1", "{}")
                )
            ]),
            new ToolResultsMessage(
                content: null,
                results: [
                    ToolResult.FromText(
                        toolName,
                        "call-1",
                        ToolExecutionStatus.Success,
                        "ok"
                    )
                ]
            )
        ]);
        var observer = new CompletionStreamObserver();
        var observerEventCount = 0;
        observer.ReceivedTextDelta += _ => observerEventCount++;
        observer.ReceivedReasoningDelta += _ => observerEventCount++;
        observer.ReceivedThinkingBegin += () => observerEventCount++;
        observer.ReceivedThinkingEnd += () => observerEventCount++;
        observer.ReceivedToolCall += _ => observerEventCount++;

        CompletionRequestRejectedException exception = await Assert.ThrowsAsync<
            CompletionRequestRejectedException
        >(() =>
            client.StreamCompletionAsync(
                request,
                observer,
                CancellationToken.None
            )
        );

        AssertInvalidFunctionNameRejection(exception);
        Assert.Equal(0, observerEventCount);
        Assert.Equal(0, provider.CallCount);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("recap_grid.control")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task StreamCompletionAsync_InvalidCurrentToolNameIsKnownNoDispatchRejection(
        string toolName
    ) {
        CodexSubscriptionCredential credential = Credential(
            "token",
            "account",
            1
        );
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => CompletedResponse("unused"));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );
        var invalidTool = new ToolDefinition(
            toolName,
            "Invalid Responses function name.",
            new ToolSchema.Object()
        );
        var request = new CompletionRequest(
            "gpt-test",
            new CompletionPromptPrefix(
                "system",
                CompletionOutputContract.ProviderDefault([invalidTool]),
                [new ObservationMessage("Use the tool.")]
            ),
            tailMessages: []
        );
        var observer = new CompletionStreamObserver();
        var observerEventCount = 0;
        observer.ReceivedTextDelta += _ => observerEventCount++;
        observer.ReceivedReasoningDelta += _ => observerEventCount++;
        observer.ReceivedThinkingBegin += () => observerEventCount++;
        observer.ReceivedThinkingEnd += () => observerEventCount++;
        observer.ReceivedToolCall += _ => observerEventCount++;

        CompletionRequestRejectedException exception = await Assert.ThrowsAsync<
            CompletionRequestRejectedException
        >(() => client.StreamCompletionAsync(
            request,
            observer,
            CancellationToken.None
        ));

        AssertInvalidFunctionNameRejection(exception);
        Assert.Equal(0, observerEventCount);
        Assert.Equal(0, provider.CallCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamCompletionAsync_UnauthorizedReloadsChangedGenerationOnceWithIdenticalBody() {
        CodexSubscriptionCredential first = Credential("token-a", "account", 1);
        CodexSubscriptionCredential second = Credential("token-b", "account", 2);
        var provider = new ScriptedCredentialProvider(
            call => call == 1 ? first : second
        );
        var handler = new CapturingHandler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : CompletedResponse("OK"));
        using var client = CreateClient(
            provider,
            handler,
            first.AccountFingerprint
        );

        CompletionResult result = await client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        );

        Assert.Equal(2, provider.CallCount);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("Bearer token-a", request.Authorization),
            request => Assert.Equal("Bearer token-b", request.Authorization)
        );
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        Assert.Equal("OK", Assert.IsType<ActionBlock.Text>(Assert.Single(result.Message.Blocks)).Content);
    }

    [Fact]
    public async Task StreamCompletionAsync_UnauthorizedWithUnchangedGenerationDoesNotRetry() {
        CodexSubscriptionCredential credential = Credential("token-a", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );
        var observer = new CompletionStreamObserver();
        var observerEventCount = 0;
        observer.ReceivedTextDelta += _ => observerEventCount++;
        observer.ReceivedReasoningDelta += _ => observerEventCount++;
        observer.ReceivedThinkingBegin += () => observerEventCount++;
        observer.ReceivedThinkingEnd += () => observerEventCount++;
        observer.ReceivedToolCall += _ => observerEventCount++;

        CompletionRequestRejectedException exception = await Assert.ThrowsAsync<
            CompletionRequestRejectedException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer,
            CancellationToken.None
        ));

        Assert.Equal(
            "openai.codex.authentication-rejected",
            exception.Termination.ProviderReason
        );
        Assert.Equal(["http-status=401"], exception.Errors);
        Assert.Equal(0, observerEventCount);
        Assert.Equal(2, provider.CallCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamCompletionAsync_SecondUnauthorizedIsTerminalAfterExactlyTwoRequests() {
        CodexSubscriptionCredential first = Credential("token-a", "account", 1);
        CodexSubscriptionCredential second = Credential("token-b", "account", 2);
        var provider = new ScriptedCredentialProvider(
            call => call == 1 ? first : second
        );
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = CreateClient(
            provider,
            handler,
            first.AccountFingerprint
        );

        CompletionRequestRejectedException exception = await Assert.ThrowsAsync<
            CompletionRequestRejectedException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        ));

        Assert.Equal(
            "openai.codex.authentication-rejected",
            exception.Termination.ProviderReason
        );
        Assert.Equal(["http-status=401"], exception.Errors);
        Assert.Equal(2, provider.CallCount);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(400, OpenAICodexResponsesFailureReason.BackendFailure)]
    [InlineData(408, OpenAICodexResponsesFailureReason.BackendFailure)]
    [InlineData(409, OpenAICodexResponsesFailureReason.BackendFailure)]
    [InlineData(422, OpenAICodexResponsesFailureReason.BackendFailure)]
    [InlineData(500, OpenAICodexResponsesFailureReason.BackendFailure)]
    [InlineData(301, OpenAICodexResponsesFailureReason.UnexpectedBackendRedirect)]
    [InlineData(302, OpenAICodexResponsesFailureReason.UnexpectedBackendRedirect)]
    [InlineData(303, OpenAICodexResponsesFailureReason.UnexpectedBackendRedirect)]
    [InlineData(307, OpenAICodexResponsesFailureReason.UnexpectedBackendRedirect)]
    [InlineData(308, OpenAICodexResponsesFailureReason.UnexpectedBackendRedirect)]
    public async Task StreamCompletionAsync_NonRetryableStatusSendsOneRequest(
        int statusCode,
        OpenAICodexResponsesFailureReason expectedReason
    ) {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            (HttpStatusCode)statusCode
        ) {
            Content = new StringContent("PROVIDER_ERROR_CANARY")
        });
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        OpenAICodexResponsesException exception = await Assert.ThrowsAsync<
            OpenAICodexResponsesException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        ));

        Assert.Equal(expectedReason, exception.Reason);
        Assert.DoesNotContain(
            "PROVIDER_ERROR_CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(403, "openai.codex.access-denied")]
    [InlineData(429, "openai.codex.rate-limited")]
    public async Task StreamCompletionAsync_AuthoritativePreStreamStatusIsTypedKnownRejection(
        int statusCode,
        string expectedProviderReason
    ) {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => {
            var response = new HttpResponseMessage(
                (HttpStatusCode)statusCode
            ) {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"PROVIDER_MESSAGE_CANARY\","
                    + "\"code\":\"ASCII_SECRET_CODE_CANARY\","
                    + "\"type\":\"ASCII_SECRET_TYPE_CANARY\","
                    + "\"param\":\"$ASCII_SECRET_PARAM_CANARY\"}}",
                    Encoding.UTF8,
                    "application/json"
                )
            };
            response.Headers.TryAddWithoutValidation(
                "x-request-id",
                "ASCII_SECRET_REQUEST_CANARY"
            );
            if (statusCode == 429) {
                response.Headers.RetryAfter = new(
                    TimeSpan.FromSeconds(5)
                );
            }
            return response;
        });
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );
        var observer = new CompletionStreamObserver();
        var observerEventCount = 0;
        observer.ReceivedTextDelta += _ => observerEventCount++;
        observer.ReceivedReasoningDelta += _ => observerEventCount++;
        observer.ReceivedThinkingBegin += () => observerEventCount++;
        observer.ReceivedThinkingEnd += () => observerEventCount++;
        observer.ReceivedToolCall += _ => observerEventCount++;

        CompletionRequestRejectedException exception = await Assert.ThrowsAsync<
            CompletionRequestRejectedException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer,
            CancellationToken.None
        ));

        Assert.Equal(
            CompletionTerminationKind.Failed,
            exception.Termination.Kind
        );
        Assert.Equal(
            expectedProviderReason,
            exception.Termination.ProviderReason
        );
        Assert.Equal([$"http-status={statusCode}"], exception.Errors);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            "PROVIDER_MESSAGE_CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ASCII_SECRET",
            exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ASCII_SECRET",
            exception.Termination.Detail ?? string.Empty,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ASCII_SECRET",
            string.Join("\n", exception.Errors),
            StringComparison.Ordinal
        );
        Assert.Equal(0, observerEventCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamCompletionAsync_NonSuccessKeepsProviderMetadataOutOfExceptionTextAndConsumesBodyForRawTee() {
        const string rawBody =
            "{\"error\":{\"message\":\"PROMPT_OR_ACCOUNT_CANARY\","
            + "\"code\":\"ASCII_SECRET_CODE_CANARY\","
            + "\"type\":\"ASCII_SECRET_TYPE_CANARY\","
            + "\"param\":\"$ASCII_SECRET_PARAM_CANARY\"},"
            + "\"access_token\":\"ACCESS_TOKEN_CANARY\"}";
        CodexSubscriptionCredential credential = Credential(
            "token",
            "account",
            1
        );
        var provider = new ScriptedCredentialProvider(_ => credential);
        var backend = new CapturingHandler(_ => {
            var response = new HttpResponseMessage(
                HttpStatusCode.BadRequest
            ) {
                Content = new StringContent(
                    rawBody,
                    Encoding.UTF8,
                    "application/json"
                )
            };
            response.Headers.TryAddWithoutValidation(
                "x-request-id",
                "ASCII_SECRET_REQUEST_CANARY"
            );
            return response;
        });
        var rawSink = new InMemoryCompletionHttpExchangeSink();
        HttpMessageHandler pipeline = new CompletionHttpClientBuilder()
            .UsePrimaryHandler(backend)
            .AddExchangeSink(rawSink)
            .BuildHandler();
        using var client = CreateClient(
            provider,
            pipeline,
            credential.AccountFingerprint
        );

        OpenAICodexResponsesException exception = await Assert.ThrowsAsync<
            OpenAICodexResponsesException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        ));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("ASCII_SECRET_CODE_CANARY", exception.ProviderErrorCode);
        Assert.Equal("ASCII_SECRET_TYPE_CANARY", exception.ProviderErrorType);
        Assert.Equal("$ASCII_SECRET_PARAM_CANARY", exception.ProviderErrorParameter);
        Assert.Equal("ASCII_SECRET_REQUEST_CANARY", exception.ProviderRequestId);
        Assert.DoesNotContain(
            "ASCII_SECRET",
            exception.Message,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ASCII_SECRET",
            exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "PROMPT_OR_ACCOUNT_CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ACCESS_TOKEN_CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );

        CompletionHttpExchange exchange = Assert.Single(
            rawSink.GetSnapshot()
        );
        Assert.Equal(rawBody, exchange.ResponseText);
    }

    [Fact]
    public async Task StreamCompletionAsync_DetailOnlyBadRequestRemainsOutcomeUnknown() {
        CodexSubscriptionCredential credential = Credential(
            "token",
            "account",
            1
        );
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            HttpStatusCode.BadRequest
        ) {
            Content = new StringContent(
                "{\"detail\":\"PROVIDER_DETAIL_CANARY\"}",
                Encoding.UTF8,
                "application/json"
            )
        });
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        OpenAICodexResponsesException exception = await Assert.ThrowsAsync<
            OpenAICodexResponsesException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        ));

        Assert.Equal(
            OpenAICodexResponsesFailureReason.BackendFailure,
            exception.Reason
        );
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Null(exception.ProviderErrorCode);
        Assert.Null(exception.ProviderErrorType);
        Assert.Null(exception.ProviderErrorParameter);
        Assert.DoesNotContain(
            "PROVIDER_DETAIL_CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_NonSuccessDropsOversizedBodyDiagnostics() {
        string oversizedBody =
            "{\"error\":{\"message\":\"SECRET_CANARY\","
            + "\"code\":\"unsafe\\ncode\"},\"padding\":\""
            + new string('x', 17 * 1024)
            + "\"}";
        CodexSubscriptionCredential credential = Credential(
            "token",
            "account",
            1
        );
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => {
            var response = new HttpResponseMessage(
                HttpStatusCode.BadRequest
            ) {
                Content = new StringContent(
                    oversizedBody,
                    Encoding.UTF8,
                    "application/json"
                )
            };
            response.Headers.TryAddWithoutValidation(
                "x-request-id",
                "unsafe@request"
            );
            return response;
        });
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        OpenAICodexResponsesException exception = await Assert.ThrowsAsync<
            OpenAICodexResponsesException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        ));

        Assert.Null(exception.ProviderErrorCode);
        Assert.Null(exception.ProviderErrorType);
        Assert.Null(exception.ProviderErrorParameter);
        Assert.Null(exception.ProviderRequestId);
        Assert.DoesNotContain(
            "SECRET_CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_NonSuccessDropsUnsafeDiagnosticTokens() {
        const string rawBody =
            "{\"error\":{\"message\":\"MESSAGE_CANARY\","
            + "\"code\":\"UNSAFE CODE CANARY\","
            + "\"type\":\"unsafe\\ntype\","
            + "\"param\":\"unsafe@param\"}}";
        CodexSubscriptionCredential credential = Credential(
            "token",
            "account",
            1
        );
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => {
            var response = new HttpResponseMessage(
                HttpStatusCode.BadRequest
            ) {
                Content = new StringContent(
                    rawBody,
                    Encoding.UTF8,
                    "application/json"
                )
            };
            response.Headers.TryAddWithoutValidation(
                "x-request-id",
                "unsafe@request"
            );
            return response;
        });
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        OpenAICodexResponsesException exception = await Assert.ThrowsAsync<
            OpenAICodexResponsesException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        ));

        Assert.Null(exception.ProviderErrorCode);
        Assert.Null(exception.ProviderErrorType);
        Assert.Null(exception.ProviderErrorParameter);
        Assert.Null(exception.ProviderRequestId);
        Assert.DoesNotContain(
            "CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_SanitizesSseProviderErrorMessage() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => EventStreamResponse(
            """
            data: {"type":"response.failed","response":{"error":{"message":"PROVIDER_ERROR_CANARY"}}}

            """
        ));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        CompletionResult result = await client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        );

        Assert.NotNull(result.Errors);
        Assert.DoesNotContain(
            "PROVIDER_ERROR_CANARY",
            string.Join("\n", result.Errors!),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "ChatGPT Codex response failed.",
            result.Errors!
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_TypedRefusalKeepsBodyOutOfCodexTerminationMetadata() {
        const string refusalBody = "REFUSAL_BODY_ASCII_SECRET_CANARY";
        CodexSubscriptionCredential credential = Credential(
            "token",
            "account",
            1
        );
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => EventStreamResponse(
            $$"""
            event: response.refusal.done
            data: {"type":"response.refusal.done","item_id":"msg_1","content_index":0,"refusal":{{JsonSerializer.Serialize(refusalBody)}}}

            event: response.completed
            data: {"type":"response.completed"}

            """
        ));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        CompletionResult result = await client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        );

        Assert.Equal(
            CompletionTerminationKind.Incomplete,
            result.Termination.Kind
        );
        Assert.Equal("response.refusal", result.Termination.ProviderReason);
        Assert.Equal(
            "ChatGPT Codex returned a typed refusal.",
            result.Termination.Detail
        );
        Assert.Equal(refusalBody, result.Message.GetFlattenedText());
        Assert.Null(result.Errors);
        string terminationMetadata = string.Join(
            "\n",
            result.Termination.ProviderReason,
            result.Termination.Detail,
            result.Errors is null ? string.Empty : string.Join("\n", result.Errors)
        );
        Assert.DoesNotContain(
            refusalBody,
            terminationMetadata,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_RejectsPublicResponsesReasoningBeforeCredentialRead() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => CompletedResponse("unused"));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );
        var publicReasoning = new OpenAIResponsesReasoningBlock(
            """{"id":"rs_1","type":"reasoning","summary":[],"encrypted_content":"opaque"}""",
            new CompletionDescriptor(
                "openai",
                "openai-responses-v2",
                "gpt-test"
            )
        );
        CompletionRequest request = Request(
            sharedContext: [new ActionMessage([publicReasoning])]
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.StreamCompletionAsync(
                request,
                observer: null,
                CancellationToken.None
            )
        );

        Assert.Equal(0, provider.CallCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamCompletionAsync_RequiredNamedToolChoiceFailsBeforeCredentialRead() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => CompletedResponse("unused"));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );
        var tool = new ToolDefinition(
            "emit_result",
            "Emit one result.",
            new ToolSchema.Object()
        );
        var request = new CompletionRequest(
            "gpt-test",
            new CompletionPromptPrefix(
                "system",
                new CompletionOutputContract(
                    [tool],
                    CompletionToolChoice.RequiredNamed("emit_result")
                ),
                [new ObservationMessage("emit")]
            ),
            tailMessages: []
        );

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.StreamCompletionAsync(
                request,
                observer: null,
                CancellationToken.None
            )
        );

        Assert.Equal(0, provider.CallCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamCompletionAsync_EightConcurrentUnauthorizedCallsShareOneReload() {
        CodexSubscriptionCredential first = Credential("token-a", "account", 1);
        CodexSubscriptionCredential second = Credential("token-b", "account", 2);
        var provider = new ScriptedCredentialProvider(
            call => call <= 8 ? first : second
        );
        var handler = new ConcurrentUnauthorizedHandler(
            firstToken: "Bearer token-a",
            expectedInitialCalls: 8
        );
        using var client = CreateClient(
            provider,
            handler,
            first.AccountFingerprint,
            maxConcurrentRequests: 8
        );

        CompletionResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ =>
                client.StreamCompletionAsync(
                    Request(),
                    observer: null,
                    CancellationToken.None
                )
            )
        );

        Assert.Equal(9, provider.CallCount);
        Assert.Equal(16, handler.RequestCount);
        Assert.Equal(8, handler.MaxObservedInFlight);
        Assert.All(results, result => Assert.Equal(
            "OK",
            Assert.IsType<ActionBlock.Text>(
                Assert.Single(result.Message.Blocks)
            ).Content
        ));
    }

    [Fact]
    public async Task StreamCompletionAsync_TransportFailureDoesNotExposeInnerCanary() {
        CodexSubscriptionCredential credential = Credential(
            "ACCESS_CANARY",
            "ACCOUNT_CANARY",
            1
        );
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new ThrowingHandler(
            "transport echoed Bearer ACCESS_CANARY ACCOUNT_CANARY"
        );
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        OpenAICodexResponsesException exception = await Assert.ThrowsAsync<
            OpenAICodexResponsesException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        ));

        Assert.Equal(
            OpenAICodexResponsesFailureReason.TransportOutcomeUnknown,
            exception.Reason
        );
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            "ACCESS_CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ACCOUNT_CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_ProtocolFailureDoesNotExposeEventCanary() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => EventStreamResponse(
            """
            event: PROVIDER_EVENT_CANARY
            data: {"type":"response.completed"}

            """
        ));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        OpenAICodexResponsesException exception = await Assert.ThrowsAsync<
            OpenAICodexResponsesException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        ));

        Assert.Equal(
            OpenAICodexResponsesFailureReason.ProtocolCompatibilityFailure,
            exception.Reason
        );
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            "PROVIDER_EVENT_CANARY",
            exception.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_MissingContentTypeStillRequiresValidSseTerminal() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var response = CompletedResponse("OK");
        response.Content.Headers.ContentType = null;
        var handler = new CapturingHandler(_ => response);
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        CompletionResult result = await client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        );

        Assert.Equal(
            "OK",
            Assert.IsType<ActionBlock.Text>(
                Assert.Single(result.Message.Blocks)
            ).Content
        );
    }

    [Fact]
    public async Task StreamCompletionAsync_RejectsExplicitNonSseContentType() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var response = CompletedResponse("OK");
        response.Content.Headers.ContentType = new(
            "application/json"
        );
        var handler = new CapturingHandler(_ => response);
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        OpenAICodexResponsesException exception = await Assert.ThrowsAsync<
            OpenAICodexResponsesException
        >(() => client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        ));

        Assert.Equal(
            OpenAICodexResponsesFailureReason.ProtocolCompatibilityFailure,
            exception.Reason
        );
        Assert.Contains("content category: json", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, "none", null)]
    [InlineData(CompletionReasoningEffort.Low, "low", "auto")]
    [InlineData(CompletionReasoningEffort.Max, "xhigh", "auto")]
    public async Task StreamCompletionAsync_UsesCodexReasoningMappingEntry(
        CompletionReasoningEffort effort,
        string expectedEffort,
        string? expectedSummary
    ) {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => CompletedResponse("OK"));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint,
            reasoningEffort: effort
        );

        _ = await client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        );

        using JsonDocument body = JsonDocument.Parse(
            Assert.Single(handler.Requests).Body
        );
        JsonElement reasoning = body.RootElement.GetProperty("reasoning");
        Assert.Equal(expectedEffort, reasoning.GetProperty("effort").GetString());
        if (expectedSummary is null) {
            Assert.False(reasoning.TryGetProperty("summary", out _));
        }
        else {
            Assert.Equal(
                expectedSummary,
                reasoning.GetProperty("summary").GetString()
            );
        }
    }

    [Theory]
    [InlineData(PromptCacheReuseHint.ConnectionDefault)]
    [InlineData(PromptCacheReuseHint.NoReuseExpected)]
    [InlineData(PromptCacheReuseHint.ReuseExpectedSoon)]
    [InlineData(PromptCacheReuseHint.ReuseExpectedAfterPause)]
    public async Task StreamCompletionAsync_AcceptsEveryValidPromptCacheHintAsNoOp(
        PromptCacheReuseHint hint
    ) {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CapturingHandler(_ => CompletedResponse("OK"));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );

        CompletionResult result = await client.StreamCompletionAsync(
            Request(),
            new CompletionInvocationOptions {
                PromptCacheReuseHint = hint
            },
            observer: null,
            CancellationToken.None
        );

        Assert.Equal(
            PromptCacheSupportStatus.Unknown,
            result.Usage.PromptCache.SupportStatus
        );
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamCompletionAsync_CancellationWhileWaitingForGatePreservesCallerToken() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new GateBlockingHandler();
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint,
            maxConcurrentRequests: 1
        );
        Task<CompletionResult> first = client.StreamCompletionAsync(
            Request(),
            observer: null,
            CancellationToken.None
        );
        await handler.Entered;

        using var caller = new CancellationTokenSource();
        Task<CompletionResult> waiting = client.StreamCompletionAsync(
            Request(),
            observer: null,
            caller.Token
        );
        caller.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<
            OperationCanceledException
        >(() => waiting);
        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(1, provider.CallCount);

        handler.Release();
        _ = await first;
    }

    [Fact]
    public async Task StreamCompletionAsync_HttpCancellationPreservesCallerToken() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var handler = new CancellationWaitingHandler();
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );
        using var caller = new CancellationTokenSource();
        Task<CompletionResult> operation = client.StreamCompletionAsync(
            Request(),
            observer: null,
            caller.Token
        );
        await handler.Entered;
        caller.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<
            OperationCanceledException
        >(() => operation);
        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task StreamCompletionAsync_SseCancellationPreservesCallerToken() {
        CodexSubscriptionCredential credential = Credential("token", "account", 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        var stream = new CancellationWaitingStream();
        var handler = new CapturingHandler(_ => new HttpResponseMessage(
            HttpStatusCode.OK
        ) {
            Content = new StreamContent(stream) {
                Headers = { ContentType = new("text/event-stream") }
            }
        });
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );
        using var caller = new CancellationTokenSource();
        Task<CompletionResult> operation = client.StreamCompletionAsync(
            Request(),
            observer: null,
            caller.Token
        );
        await stream.Entered;
        caller.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<
            OperationCanceledException
        >(() => operation);
        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task LoggingCompletionClient_DoesNotPersistCredentialOrProviderErrorCanaries() {
        const string access = "ACCESS_LOG_CANARY";
        const string account = "ACCOUNT_LOG_CANARY";
        const string providerError = "PROVIDER_LOG_CANARY";
        CodexSubscriptionCredential credential = Credential(access, account, 1);
        var provider = new ScriptedCredentialProvider(_ => credential);
        string failedEvent = JsonSerializer.Serialize(new {
            type = "response.failed",
            response = new { error = new { message = providerError } }
        });
        var handler = new CapturingHandler(_ => EventStreamResponse(
            $"data: {failedEvent}\n\n"
        ));
        using var client = CreateClient(
            provider,
            handler,
            credential.AccountFingerprint
        );
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"atelia-codex-call-log-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        try {
            var logging = new LoggingCompletionClient(
                client,
                new CompletionConnectionConfig(
                    "codex",
                    CodexSubscriptionCompletionClientFactory.ConnectionKind,
                    "gpt-test",
                    CodexSubscriptionCompletionClientFactory.CompletionSurfaceId,
                    CodexSubscriptionCompletionClientFactory.CanonicalBaseAddress
                ),
                directory
            );

            _ = await logging.StreamCompletionAsync(
                Request(),
                observer: null,
                CancellationToken.None
            );

            string log = File.ReadAllText(
                Assert.Single(logging.WrittenCallLogPaths)
            );
            Assert.DoesNotContain(access, log, StringComparison.Ordinal);
            Assert.DoesNotContain(account, log, StringComparison.Ordinal);
            Assert.DoesNotContain(providerError, log, StringComparison.Ordinal);
            Assert.Contains(
                "ChatGPT Codex response failed.",
                log,
                StringComparison.Ordinal
            );
        }
        finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static OpenAICodexResponsesClient CreateClient(
        ICodexSubscriptionCredentialProvider provider,
        HttpMessageHandler handler,
        string expectedAccountFingerprint,
        string originator = "atelia",
        int maxConcurrentRequests = 3,
        CompletionReasoningEffort reasoningEffort =
            CompletionReasoningEffort.ProviderDefault
    ) => new(
        provider,
        new OpenAICodexResponsesClientOptions {
            ExpectedAccountFingerprint = expectedAccountFingerprint,
            Originator = originator,
            MaxConcurrentRequests = maxConcurrentRequests,
            ProductVersion = "test",
            ReasoningEffort = reasoningEffort
        },
        handler
    );

    private static CodexSubscriptionCredential Credential(
        string token,
        string account,
        long generation
    ) => CodexSubscriptionCredential.Create(
        token,
        account,
        residency: null,
        expiresAt: DateTimeOffset.UtcNow.AddHours(1),
        stableGeneration: generation
    );

    private static CompletionRequest Request(
        int? maxTokens = null,
        IReadOnlyList<IHistoryMessage>? sharedContext = null
    ) => new(
        "gpt-test",
        new CompletionPromptPrefix(
            "system",
            CompletionOutputContract.ProviderDefault(
                ImmutableArray<ToolDefinition>.Empty
            ),
            sharedContext ?? [new ObservationMessage("Reply exactly OK.")]
        ),
        tailMessages: [],
        maxTokens: maxTokens
    );

    private static void AssertInvalidFunctionNameRejection(
        CompletionRequestRejectedException exception
    ) {
        Assert.Equal(
            "openai.responses.invalid-function-name",
            exception.Termination.ProviderReason
        );
        Assert.Contains(
            "1-64 ASCII letters",
            exception.Termination.Detail ?? string.Empty,
            StringComparison.Ordinal
        );
        Assert.Equal(["adapter-validation=function-name"], exception.Errors);
        Assert.Null(exception.InnerException);
    }

    private static HttpResponseMessage CompletedResponse(string text) =>
        EventStreamResponse($$"""
        data: {"type":"response.output_text.delta","delta":{{JsonSerializer.Serialize(text)}}}

        data: {"type":"response.completed"}

        """);

    private static HttpResponseMessage EventStreamResponse(string content) =>
        new(HttpStatusCode.OK) {
            Content = new StringContent(
                content.TrimEnd('\r', '\n') + "\n\n",
                Encoding.UTF8,
                "text/event-stream"
            )
        };

    private sealed class ScriptedCredentialProvider(
        Func<int, CodexSubscriptionCredential> resolve
    ) : ICodexSubscriptionCredentialProvider {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<CodexSubscriptionCredential> GetCredentialAsync(
            CancellationToken cancellationToken = default
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(resolve(call));
        }
    }

    private sealed class CapturingHandler(
        Func<int, HttpResponseMessage> respond
    ) : HttpMessageHandler {
        private int _callCount;

        public ConcurrentQueue<CapturedRequest> Captured { get; } = new();

        public IReadOnlyList<CapturedRequest> Requests => [.. Captured];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(
                static pair => pair.Key,
                static pair => string.Join(",", pair.Value),
                StringComparer.OrdinalIgnoreCase
            );
            Captured.Enqueue(new CapturedRequest(
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.ToString(),
                headers,
                body
            ));
            return respond(Interlocked.Increment(ref _callCount));
        }
    }

    private sealed record CapturedRequest(
        string Uri,
        string? Authorization,
        string Accept,
        IReadOnlyDictionary<string, string> Headers,
        string Body
    ) {
        public string Header(string name) => Headers[name];
    }

    private sealed class ConcurrentUnauthorizedHandler(
        string firstToken,
        int expectedInitialCalls
    ) : HttpMessageHandler {
        private readonly TaskCompletionSource _allInitialEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _initialCalls;
        private int _requestCount;
        private int _currentInFlight;
        private int _maxObservedInFlight;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public int MaxObservedInFlight => Volatile.Read(
            ref _maxObservedInFlight
        );

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            _ = Interlocked.Increment(ref _requestCount);
            int inFlight = Interlocked.Increment(ref _currentInFlight);
            UpdateMaximum(inFlight);
            try {
                string authorization = request.Headers.Authorization
                    ?.ToString() ?? string.Empty;
                if (string.Equals(
                        authorization,
                        firstToken,
                        StringComparison.Ordinal
                    )) {
                    if (Interlocked.Increment(ref _initialCalls)
                        == expectedInitialCalls) {
                        _allInitialEntered.TrySetResult();
                    }
                    await _allInitialEntered.Task.WaitAsync(
                        cancellationToken
                    );
                    return new HttpResponseMessage(
                        HttpStatusCode.Unauthorized
                    );
                }
                return CompletedResponse("OK");
            }
            finally {
                _ = Interlocked.Decrement(ref _currentInFlight);
            }
        }

        private void UpdateMaximum(int value) {
            int observed;
            do {
                observed = Volatile.Read(ref _maxObservedInFlight);
                if (value <= observed) { return; }
            } while (Interlocked.CompareExchange(
                ref _maxObservedInFlight,
                value,
                observed
            ) != observed);
        }
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException(message);
    }

    private sealed class GateBlockingHandler : HttpMessageHandler {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return CompletedResponse("OK");
        }
    }

    private sealed class CancellationWaitingHandler : HttpMessageHandler {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task Entered => _entered.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException(
                "An infinite HTTP wait returned without cancellation."
            );
        }
    }

    private sealed class CancellationWaitingStream : Stream {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task Entered => _entered.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();
        public override void SetLength(long value)
            => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
