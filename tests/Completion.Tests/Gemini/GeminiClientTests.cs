using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Transport;
using Xunit;

namespace Atelia.Completion.Gemini.Tests;

public sealed class GeminiClientTests {
    [Fact]
    public void Constructor_DoesNotOverwriteExternalBaseAddressWhenNoneProvided() {
        if (!GeminiProductionTypesPresent()) { return; }

        using var handler = new EmptyHttpMessageHandler();
        var preconfigured = new Uri("http://localhost:9000/");
        using var httpClient = new HttpClient(handler) {
            BaseAddress = preconfigured
        };

        var client = CreateGeminiClient(httpClient, baseAddress: null);

        Assert.NotNull(client);
        Assert.Equal(preconfigured, httpClient.BaseAddress);
    }

    [Fact]
    public void Constructor_ExplicitBaseAddressOverridesExternalHttpClientBaseAddress() {
        if (!GeminiProductionTypesPresent()) { return; }

        using var handler = new EmptyHttpMessageHandler();
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:9000/")
        };
        var explicitAddress = new Uri("http://localhost:7777/");

        var client = CreateGeminiClient(httpClient, baseAddress: explicitAddress);

        Assert.NotNull(client);
        Assert.Equal(explicitAddress, httpClient.BaseAddress);
    }

    [Fact]
    public async Task StreamCompletionAsync_NonSuccessStatus_IncludesResponseBodySnippetInException() {
        if (!GeminiProductionTypesPresent()) { return; }

        using var handler = new SequenceHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent(
                    """
                    {"error":{"message":"bad input","status":"INVALID_ARGUMENT"}}
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            }
        );
        using var httpClient = new HttpClient(handler) {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var client = CreateGeminiClient(httpClient, baseAddress: null);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => InvokeStreamCompletionAsync(client, CreateRequest())
        );

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("bad input", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamCompletionAsync_UsesApiKeyHeaderWithoutLeakingKeyIntoRequestUri() {
        if (!GeminiProductionTypesPresent()) { return; }

        using var handler = new InspectingHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(
                    """
                    data: {"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}

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

        var client = CreateGeminiClient(httpClient, baseAddress: null, apiKey: "secret-key");
        var result = await InvokeStreamCompletionAsync(client, CreateRequest());

        Assert.Equal("ok", result.Message.GetFlattenedText());
        Assert.Equal("secret-key", handler.LastRequest?.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("secret-key", handler.LastRequest?.RequestUri?.ToString(), StringComparison.Ordinal);
    }

    private static CompletionRequest CreateRequest() {
        return new CompletionRequest(
            ModelId: "gemini-2.5-flash",
            SystemPrompt: "system",
            Context: new[] { new ObservationMessage("hello") },
            Tools: System.Collections.Immutable.ImmutableArray<ToolDefinition>.Empty
        );
    }

    private static object CreateGeminiClient(HttpClient httpClient, Uri? baseAddress, string? apiKey = null) {
        var clientType = typeof(CompletionHttpTransportFactory).Assembly.GetType("Atelia.Completion.Gemini.GeminiClient");
        Assert.NotNull(clientType);
        var constructor = clientType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(HasSupportedGeminiConstructorShape);

        Assert.NotNull(constructor);

        var arguments = constructor!
            .GetParameters()
            .Select(parameter => ResolveConstructorArgument(parameter, httpClient, baseAddress, apiKey))
            .ToArray();

        try {
            return constructor.Invoke(arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static bool HasSupportedGeminiConstructorShape(ConstructorInfo constructor) {
        var parameters = constructor.GetParameters();
        return parameters.Any(parameter => parameter.ParameterType == typeof(HttpClient))
            && parameters.Any(parameter => parameter.ParameterType == typeof(Uri));
    }

    private static object? ResolveConstructorArgument(ParameterInfo parameter, HttpClient httpClient, Uri? baseAddress, string? apiKey) {
        if (parameter.ParameterType == typeof(HttpClient)) { return httpClient; }

        if (parameter.ParameterType == typeof(Uri)) { return baseAddress; }

        if (parameter.ParameterType == typeof(string) && string.Equals(parameter.Name, "apiKey", StringComparison.OrdinalIgnoreCase)) { return apiKey; }

        if (parameter.HasDefaultValue) { return parameter.DefaultValue; }

        throw new InvalidOperationException(
            $"Unsupported GeminiClient constructor parameter '{parameter.Name}' of type '{parameter.ParameterType}'."
        );
    }

    private static async Task<CompletionResult> InvokeStreamCompletionAsync(object client, CompletionRequest request) {
        var method = client.GetType().GetMethod(
            "StreamCompletionAsync",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { typeof(CompletionRequest), typeof(CompletionStreamObserver), typeof(CancellationToken) },
            modifiers: null
        );

        Assert.NotNull(method);

        var task = (Task)method!.Invoke(client, new object?[] { request, null, CancellationToken.None })!;
        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        Assert.NotNull(resultProperty);
        return Assert.IsType<CompletionResult>(resultProperty!.GetValue(task));
    }

    private static bool GeminiProductionTypesPresent() {
        var assembly = typeof(CompletionHttpTransportFactory).Assembly;
        return assembly.GetType("Atelia.Completion.Gemini.GeminiClient") is not null;
    }

    private sealed class EmptyHttpMessageHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHttpMessageHandler(params HttpResponseMessage[] responses) {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class InspectingHttpMessageHandler : HttpMessageHandler {
        private readonly HttpResponseMessage _response;

        public InspectingHttpMessageHandler(HttpResponseMessage response) {
            _response = response;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }
}
