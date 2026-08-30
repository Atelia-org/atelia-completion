using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Completion.OpenAI;

/// <summary>
/// Direct SSE client for the current ChatGPT Codex subscription backend.
/// The backend is an implementation-coupled surface rather than the public
/// OpenAI Responses API. OAuth refresh ownership remains with Codex CLI.
/// </summary>
public sealed class OpenAICodexResponsesClient : ICompletionClient,
    IDisposable {
    private const string DebugCategory = "Provider";
    private const int MaximumNonSuccessBodyBytes = 16 * 1024;
    private const int MaximumErrorTokenCharacters = 64;
    private const int MaximumErrorParameterCharacters = 128;
    private const int MaximumRequestIdCharacters = 128;
    private const string AuthenticationRejectedReason =
        "openai.codex.authentication-rejected";
    private const string AccessDeniedReason =
        "openai.codex.access-denied";
    private const string RateLimitedReason =
        "openai.codex.rate-limited";

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ICodexSubscriptionCredentialProvider _credentialProvider;
    private readonly HttpClient _httpClient;
    private readonly OpenAIResponsesProtocolClientCore _protocolCore;
    private readonly SemaphoreSlim _admissionGate;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly string _expectedAccountFingerprint;
    private readonly string _originator;
    private readonly string _userAgent;
    private UnauthorizedReloadResolution? _unauthorizedReloadResolution;
    private bool _disposed;

    public string Name => "chatgpt.com";

    public string ApiSpecId => ChatGptCodexResponsesProfile.ApiSpecId;

    public OpenAICodexResponsesClient(
        ICodexSubscriptionCredentialProvider credentialProvider,
        OpenAICodexResponsesClientOptions options
    ) : this(
        credentialProvider,
        options,
        CreateProductionHandler()
    ) { }

    internal OpenAICodexResponsesClient(
        ICodexSubscriptionCredentialProvider credentialProvider,
        OpenAICodexResponsesClientOptions options,
        HttpMessageHandler httpMessageHandler
    ) {
        ArgumentNullException.ThrowIfNull(credentialProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpMessageHandler);
        ValidateOptions(options);

        ReasoningBlockCodecs.EnsureRegistered();

        _credentialProvider = credentialProvider;
        _expectedAccountFingerprint = options.ExpectedAccountFingerprint;
        _originator = options.Originator;
        _userAgent = BuildUserAgent(options);
        _admissionGate = new SemaphoreSlim(
            options.MaxConcurrentRequests,
            options.MaxConcurrentRequests
        );
        _httpClient = new HttpClient(
            httpMessageHandler,
            disposeHandler: true
        ) {
            BaseAddress = ChatGptCodexResponsesProfile.CanonicalBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
        _protocolCore = new OpenAIResponsesProtocolClientCore(
            new OpenAIResponsesClientOptions {
                ReasoningEffort = options.ReasoningEffort,
                Store = false,
                IncludeEncryptedReasoning = true,
                ExtraBody = null
            },
            ApiSpecId,
            "ChatGPT/Codex Responses",
            "ChatGPT Codex Responses",
            ChatGptCodexResponsesProfile.MapReasoningEffort,
            supportsRequiredNamedToolChoice: false,
            sanitizeProviderErrors: true
        );

        DebugUtil.Info(
            DebugCategory,
            $"[ChatGPT/Codex Responses] Client initialized originator={_originator}, maxConcurrency={options.MaxConcurrentRequests}, reasoningEffort={options.ReasoningEffort}"
        );
    }

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) => StreamCompletionCoreAsync(
        request,
        CompletionInvocationOptions.Default,
        observer,
        cancellationToken
    );

    public Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(invocationOptions);
        invocationOptions.Validate();
        return StreamCompletionCoreAsync(
            request,
            invocationOptions,
            observer,
            cancellationToken
        );
    }

    private async Task<CompletionResult> StreamCompletionCoreAsync(
        CompletionRequest request,
        CompletionInvocationOptions invocationOptions,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.MaxTokens is not null) {
            throw new NotSupportedException(
                "ChatGPT Codex Responses has no verified provider-neutral MaxTokens mapping."
            );
        }

        await _admissionGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try {
            return await _protocolCore.StreamCompletionAsync(
                this,
                request,
                invocationOptions,
                observer,
                SendStreamingRequestAsync,
                cancellationToken
            ).ConfigureAwait(false);
        }
        finally {
            _admissionGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendStreamingRequestAsync(
        OpenAIResponsesApiRequest apiRequest,
        CancellationToken cancellationToken
    ) {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            apiRequest,
            SerializerOptions
        );
        try {
            CodexSubscriptionCredential credential =
                await _credentialProvider
                    .GetCredentialAsync(cancellationToken)
                    .ConfigureAwait(false);
            ValidateCredentialAccount(credential);
            ClearStaleUnauthorizedResolution(credential.Generation);

            HttpResponseMessage response = await SendAttemptAsync(
                body,
                credential,
                cancellationToken
            ).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized) {
                response.Dispose();
                CodexSubscriptionCredential? reloaded =
                    await ReloadAfterUnauthorizedAsync(
                        credential,
                        cancellationToken
                    ).ConfigureAwait(false);
                if (reloaded is null) {
                    throw RequestRejected(
                        HttpStatusCode.Unauthorized,
                        AuthenticationRejectedReason
                    );
                }
                response = await SendAttemptAsync(
                    body,
                    reloaded,
                    cancellationToken
                ).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode) {
                try {
                    throw await ClassifyNonSuccessAsync(
                        response,
                        cancellationToken
                    ).ConfigureAwait(false);
                }
                finally {
                    response.Dispose();
                }
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null
                && !string.Equals(
                    mediaType,
                    "text/event-stream",
                    StringComparison.OrdinalIgnoreCase
                )) {
                response.Dispose();
                throw Failure(
                    OpenAICodexResponsesFailureReason
                        .ProtocolCompatibilityFailure,
                    "ChatGPT Codex response did not use text/event-stream "
                        + $"(content category: {ClassifyContentType(mediaType)})."
                );
            }
            return response;
        }
        finally {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private async Task<HttpResponseMessage> SendAttemptAsync(
        byte[] body,
        CodexSubscriptionCredential credential,
        CancellationToken cancellationToken
    ) {
        try {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                ChatGptCodexResponsesProfile.RelativeRequestUri
            ) {
                Content = new ByteArrayContent(body)
            };
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/json") {
                    CharSet = "utf-8"
                };
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("text/event-stream")
            );
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                credential.AccessToken
            );
            AddValidatedHeader(
                request.Headers,
                "ChatGPT-Account-ID",
                credential.AccountId
            );
            AddValidatedHeader(request.Headers, "originator", _originator);
            AddValidatedHeader(request.Headers, "User-Agent", _userAgent);
            if (!string.IsNullOrWhiteSpace(credential.Residency)) {
                AddValidatedHeader(
                    request.Headers,
                    "x-openai-internal-codex-residency",
                    credential.Residency
                );
            }

            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (OpenAICodexResponsesException) {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            throw Failure(
                OpenAICodexResponsesFailureReason.TransportOutcomeUnknown,
                "ChatGPT Codex transport failed before a usable streaming response was obtained."
            );
        }
    }

    private async Task<CodexSubscriptionCredential?>
        ReloadAfterUnauthorizedAsync(
            CodexSubscriptionCredential rejected,
            CancellationToken cancellationToken
        ) {
        await _reloadGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try {
            if (Volatile.Read(ref _unauthorizedReloadResolution) is {
                    RejectedGeneration: var rejectedGeneration
                } cached
                && rejectedGeneration == rejected.Generation) {
                return cached.Replacement;
            }

            CodexSubscriptionCredential reloaded =
                await _credentialProvider
                    .GetCredentialAsync(cancellationToken)
                    .ConfigureAwait(false);
            ValidateCredentialAccount(reloaded);
            bool changed = reloaded.Generation != rejected.Generation;
            var resolution = new UnauthorizedReloadResolution(
                rejected.Generation,
                changed ? reloaded : null
            );
            Volatile.Write(ref _unauthorizedReloadResolution, resolution);
            return resolution.Replacement;
        }
        finally {
            _reloadGate.Release();
        }
    }

    private void ValidateCredentialAccount(
        CodexSubscriptionCredential credential
    ) {
        if (!string.Equals(
                credential.AccountFingerprint,
                _expectedAccountFingerprint,
                StringComparison.Ordinal
            )) {
            throw new CodexSubscriptionCredentialException(
                CodexSubscriptionCredentialFailureReason.AuthAccountChanged
            );
        }
    }

    private void ClearStaleUnauthorizedResolution(long currentGeneration) {
        UnauthorizedReloadResolution? cached = Volatile.Read(
            ref _unauthorizedReloadResolution
        );
        if (cached is null
            || cached.RejectedGeneration == currentGeneration) {
            return;
        }
        _ = Interlocked.CompareExchange(
            ref _unauthorizedReloadResolution,
            null,
            cached
        );
    }

    private static async Task<Exception>
        ClassifyNonSuccessAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken
        ) {
        HttpStatusCode status = response.StatusCode;
        BackendFailureDiagnostics diagnostics =
            await CaptureBackendFailureDiagnosticsAsync(
                response,
                cancellationToken
            ).ConfigureAwait(false);
        if ((int)status is >= 300 and < 400) {
            return Failure(
                OpenAICodexResponsesFailureReason.UnexpectedBackendRedirect,
                "ChatGPT Codex returned an unexpected redirect; redirects are disabled.",
                status,
                diagnostics: diagnostics
            );
        }
        if (status is HttpStatusCode.Unauthorized) {
            return RequestRejected(
                status,
                AuthenticationRejectedReason
            );
        }
        if (status is HttpStatusCode.Forbidden) {
            return RequestRejected(
                status,
                AccessDeniedReason
            );
        }
        if ((int)status == 429) {
            return RequestRejected(
                status,
                RateLimitedReason
            );
        }
        return Failure(
            OpenAICodexResponsesFailureReason.BackendFailure,
            $"ChatGPT Codex request failed with HTTP status {(int)status}.",
            status,
            diagnostics: diagnostics
        );
    }

    private static async Task<BackendFailureDiagnostics>
        CaptureBackendFailureDiagnosticsAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken
        ) {
        string? requestId = ReadSafeRequestId(response);
        if (response.Content is null
            || response.Content.Headers.ContentLength
                is > MaximumNonSuccessBodyBytes) {
            return new BackendFailureDiagnostics(null, null, null, requestId);
        }

        byte[] buffer = new byte[MaximumNonSuccessBodyBytes + 1];
        try {
            int length = 0;
            try {
                using Stream stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (length < buffer.Length) {
                    int read = await stream.ReadAsync(
                        buffer.AsMemory(length, buffer.Length - length),
                        cancellationToken
                    ).ConfigureAwait(false);
                    if (read == 0) { break; }
                    length += read;
                }
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception)) {
                return new BackendFailureDiagnostics(
                    null,
                    null,
                    null,
                    requestId
                );
            }

            if (length > MaximumNonSuccessBodyBytes) {
                return new BackendFailureDiagnostics(
                    null,
                    null,
                    null,
                    requestId
                );
            }

            using JsonDocument document = JsonDocument.Parse(
                buffer.AsMemory(0, length),
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                }
            );
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "error",
                    out JsonElement error)
                || error.ValueKind is not JsonValueKind.Object) {
                return new BackendFailureDiagnostics(
                    null,
                    null,
                    null,
                    requestId
                );
            }
            return new BackendFailureDiagnostics(
                ReadSafeJsonToken(
                    error,
                    "code",
                    MaximumErrorTokenCharacters
                ),
                ReadSafeJsonToken(
                    error,
                    "type",
                    MaximumErrorTokenCharacters
                ),
                ReadSafeJsonToken(
                    error,
                    "param",
                    MaximumErrorParameterCharacters
                ),
                requestId
            );
        }
        catch (JsonException) {
            return new BackendFailureDiagnostics(null, null, null, requestId);
        }
        finally {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static string? ReadSafeJsonToken(
        JsonElement owner,
        string propertyName,
        int maximumLength
    ) {
        if (!owner.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind is not JsonValueKind.String) {
            return null;
        }
        string? text = value.GetString();
        return IsSafeDiagnosticToken(text, maximumLength) ? text : null;
    }

    private static string? ReadSafeRequestId(
        HttpResponseMessage response
    ) {
        foreach (string headerName in new[] {
            "x-request-id",
            "request-id",
            "openai-request-id"
        }) {
            if (!response.Headers.TryGetValues(
                    headerName,
                    out IEnumerable<string>? values)) {
                continue;
            }
            string[] exactValues = values.Take(2).ToArray();
            if (exactValues.Length == 1
                && IsSafeDiagnosticToken(
                    exactValues[0],
                    MaximumRequestIdCharacters
                )) {
                return exactValues[0];
            }
        }
        return null;
    }

    private static bool IsSafeDiagnosticToken(
        string? value,
        int maximumLength
    ) {
        if (value is not { Length: > 0 }
            || value.Length > maximumLength) {
            return false;
        }
        return value.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.' or ':' or '/'
                or '[' or ']' or '$');
    }

    private static string ClassifyContentType(string? mediaType) {
        if (string.IsNullOrWhiteSpace(mediaType)) { return "missing"; }
        if (string.Equals(
                mediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase
            )) {
            return "json";
        }
        if (string.Equals(
                mediaType,
                "text/html",
                StringComparison.OrdinalIgnoreCase
            )) {
            return "html";
        }
        return "other";
    }

    private static OpenAICodexResponsesException Failure(
        OpenAICodexResponsesFailureReason reason,
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        BackendFailureDiagnostics? diagnostics = null
    ) => new(
        reason,
        message,
        statusCode,
        retryAfter,
        diagnostics?.Code,
        diagnostics?.Type,
        diagnostics?.Parameter,
        diagnostics?.RequestId
    );

    /// <summary>
    /// Translates only response statuses that the pinned direct backend has
    /// authoritatively completed as a pre-stream rejection. This method is
    /// called before the protocol parser can emit an observer delta. In
    /// particular, HTTP 400 is intentionally excluded: the calibrated private
    /// backend currently returns only an unsafe free-form <c>detail</c> field,
    /// which is insufficient to prove a stable rejection category.
    /// </summary>
    private static CompletionRequestRejectedException RequestRejected(
        HttpStatusCode status,
        string providerReason
    ) {
        // This exception is journaled. Character allowlists are not taint
        // sanitizers: provider-controlled ASCII can still be a secret. Keep
        // durable diagnostics strictly adapter-owned and deterministic.
        string[] errors = [$"http-status={(int)status}"];
        string detail =
            $"ChatGPT Codex rejected the request before streaming with HTTP status {(int)status}.";
        return new CompletionRequestRejectedException(
            CompletionTermination.Failed(providerReason, detail),
            errors
        );
    }

    internal static HttpMessageHandler CreateProductionHandler() =>
        new HttpClientHandler {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false
        };

    private static void ValidateOptions(
        OpenAICodexResponsesClientOptions options
    ) {
        if (!Enum.IsDefined(options.ReasoningEffort)) {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ReasoningEffort,
                "Unknown reasoning effort."
            );
        }
        if (options.MaxConcurrentRequests is < 1 or > 8) {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxConcurrentRequests,
                "MaxConcurrentRequests must be between 1 and 8."
            );
        }
        if (!IsValidOriginator(options.Originator)) {
            throw new ArgumentException(
                "Originator must match ^[a-z][a-z0-9._-]{0,63}$.",
                nameof(options)
            );
        }
        if (!IsSafeProductToken(options.ProductName, 64)) {
            throw new ArgumentException(
                "ProductName must be printable ASCII without whitespace or HTTP separators.",
                nameof(options)
            );
        }
        if (options.ProductVersion is not null
            && !IsSafeProductToken(options.ProductVersion, 64)) {
            throw new ArgumentException(
                "ProductVersion must be printable ASCII without whitespace or HTTP separators.",
                nameof(options)
            );
        }
        if (string.IsNullOrWhiteSpace(options.ExpectedAccountFingerprint)
            || options.ExpectedAccountFingerprint.Length != 71
            || !options.ExpectedAccountFingerprint.StartsWith(
                "sha256:",
                StringComparison.Ordinal
            )
            || options.ExpectedAccountFingerprint[7..]
                .Any(static c => !char.IsAsciiHexDigitLower(c))) {
            throw new ArgumentException(
                "ExpectedAccountFingerprint must be a lowercase sha256 fingerprint.",
                nameof(options)
            );
        }
    }

    private static bool IsValidOriginator(string? value) {
        if (value is not { Length: >= 1 and <= 64 }
            || value[0] is < 'a' or > 'z') {
            return false;
        }
        return value.AsSpan(1).IndexOfAnyExcept(
            "abcdefghijklmnopqrstuvwxyz0123456789._-"
        ) < 0;
    }

    private static bool IsSafeProductToken(string? value, int maximumLength) {
        if (value is not { Length: > 0 }
            || value.Length > maximumLength) {
            return false;
        }
        foreach (char c in value) {
            if (c is < '!' or > '~'
                || "()<>@,;:\\\"/[]?={}".Contains(c)) {
                return false;
            }
        }
        return true;
    }

    private static string BuildUserAgent(
        OpenAICodexResponsesClientOptions options
    ) {
        string version = options.ProductVersion
            ?? typeof(OpenAICodexResponsesClient).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+', 2)[0]
            ?? typeof(OpenAICodexResponsesClient).Assembly
                .GetName().Version?.ToString()
            ?? "0";
        if (!IsSafeProductToken(version, 64)) { version = "0"; }

        string os = SanitizeComment(RuntimeInformation.OSDescription, 80);
        string architecture = RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{options.ProductName}/{version} ({os}; {architecture})"
        );
    }

    private static string SanitizeComment(string value, int maximumLength) {
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (char c in value) {
            if (builder.Length >= maximumLength) { break; }
            builder.Append(c is >= ' ' and <= '~' and not '(' and not ')'
                ? c
                : '_');
        }
        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static void AddValidatedHeader(
        HttpRequestHeaders headers,
        string name,
        string value
    ) {
        if (!IsSafeHeaderValue(value)
            || !headers.TryAddWithoutValidation(name, value)) {
            throw Failure(
                OpenAICodexResponsesFailureReason
                    .ProtocolCompatibilityFailure,
                $"ChatGPT Codex request header '{name}' is not representable."
            );
        }
    }

    private static bool IsSafeHeaderValue(string? value) {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) {
            return false;
        }
        return value.All(static c => c is >= ' ' and <= '~'
            && c is not '\r' and not '\n');
    }

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    private sealed record UnauthorizedReloadResolution(
        long RejectedGeneration,
        CodexSubscriptionCredential? Replacement
    );

    private sealed record BackendFailureDiagnostics(
        string? Code,
        string? Type,
        string? Parameter,
        string? RequestId
    );

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _httpClient.Dispose();
        _admissionGate.Dispose();
        _reloadGate.Dispose();
    }
}
