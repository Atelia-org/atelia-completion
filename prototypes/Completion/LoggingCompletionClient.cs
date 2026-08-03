using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Completion;

public sealed record CompletionCallLogContext(
    string? Command = null,
    int? EpochIndex = null,
    long? EventOrdinal = null,
    string? MaintainerId = null,
    string? TargetCarrier = null,
    string? TargetBlockId = null
);

public sealed class LoggingCompletionClient : ICompletionClient {
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ICompletionClient _inner;
    private readonly CompletionConnectionConfig _connection;
    private readonly CompletionCallLogContext _context;
    private readonly ConcurrentQueue<string> _writtenCallLogPaths = new();
    private readonly ICompletionCallLogSink? _callLogSink;
    private readonly Action<string, Exception> _callLogFailureReporter;

    public LoggingCompletionClient(
        ICompletionClient inner,
        CompletionConnectionConfig connection,
        string callLogDir,
        CompletionCallLogContext? context = null
    ) : this(
        inner,
        connection,
        callLogDir,
        context,
        static () => new FileCompletionCallLogSink()
    ) { }

    internal LoggingCompletionClient(
        ICompletionClient inner,
        CompletionConnectionConfig connection,
        string callLogDir,
        CompletionCallLogContext? context,
        Func<ICompletionCallLogSink> callLogSinkFactory,
        Action<string, Exception>? callLogFailureReporter = null
    ) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentException.ThrowIfNullOrWhiteSpace(callLogDir);
        ArgumentNullException.ThrowIfNull(callLogSinkFactory);
        _context = context ?? new CompletionCallLogContext();
        _callLogFailureReporter = callLogFailureReporter
            ?? DefaultCallLogFailureReporter;

        try {
            ICompletionCallLogSink sink = callLogSinkFactory()
                ?? throw new InvalidOperationException(
                    "The call-log sink factory returned null."
                );
            sink.Initialize(callLogDir);
            _callLogSink = sink;
        }
        catch (Exception ex) {
            ReportCallLogFailure("initialize", ex);
        }
    }

    public string Name => _inner.Name;

    public string ApiSpecId => _inner.ApiSpecId;

    public IReadOnlyList<string> WrittenCallLogPaths
        => Array.AsReadOnly(_writtenCallLogPaths.ToArray());

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        using CompletionCallLogReservation? reservation =
            TryReserveCallLog();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        CompletionResult result;

        try {
            result = await _inner.StreamCompletionAsync(request, observer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) {
            stopwatch.Stop();
            TryWriteCallLog(
                reservation,
                startedAt,
                stopwatch.Elapsed,
                request,
                result: null,
                ex
            );
            throw;
        }

        stopwatch.Stop();
        TryWriteCallLog(
            reservation,
            startedAt,
            stopwatch.Elapsed,
            request,
            result,
            exception: null
        );
        return result;
    }

    private CompletionCallLogReservation? TryReserveCallLog() {
        if (_callLogSink is null) { return null; }

        try {
            return _callLogSink.Reserve();
        }
        catch (Exception ex) {
            ReportCallLogFailure("reserve", ex);
            return null;
        }
    }

    private void TryWriteCallLog(
        CompletionCallLogReservation? reservation,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        CompletionRequest request,
        CompletionResult? result,
        Exception? exception
    ) {
        if (reservation is null) { return; }

        try {
            var log = new CompletionCallLogEntry(
                Schema: "atelia.completion.call-log.v1",
                CallId: reservation.CallId,
                TimestampUtc: startedAt,
                ElapsedMs: (long)elapsed.TotalMilliseconds,
                Connection: CompletionCallLogConnectionSnapshot.From(
                    _connection,
                    _inner
                ),
                Context: _context,
                Request: CompletionCallLogRequest.From(request),
                Response: result is null
                    ? null
                    : CompletionCallLogResponse.From(result),
                Exception: exception is null
                    ? null
                    : CompletionCallLogException.From(exception)
            );

            JsonSerializer.Serialize(reservation.Stream, log, JsonOptions);
            reservation.Complete();
            _writtenCallLogPaths.Enqueue(reservation.Path);
        }
        catch (Exception ex) {
            ReportCallLogFailure("write", ex);
        }
    }

    private void ReportCallLogFailure(string stage, Exception exception) {
        try {
            _callLogFailureReporter(stage, exception);
        }
        catch {
            // Diagnostics for best-effort logging must also remain best-effort.
        }
    }

    private static void DefaultCallLogFailureReporter(
        string stage,
        Exception exception
    ) => DebugUtil.Warning(
        "Completion.CallLog",
        $"Completion call-log {stage} failed; provider outcome is preserved.",
        exception,
        DebugEventKind.Failure
    );
}

internal interface ICompletionCallLogSink {
    void Initialize(string callLogDirectory);

    CompletionCallLogReservation Reserve();
}

internal sealed class FileCompletionCallLogSink : ICompletionCallLogSink {
    private string? _callLogDirectory;
    private int _nextCallId;

    public void Initialize(string callLogDirectory) {
        string fullPath = Path.GetFullPath(callLogDirectory);
        Directory.CreateDirectory(fullPath);
        _nextCallId = GetMaxExistingCallId(fullPath);
        _callLogDirectory = fullPath;
    }

    public CompletionCallLogReservation Reserve() {
        string callLogDirectory = _callLogDirectory
            ?? throw new InvalidOperationException(
                "The call-log sink has not been initialized."
            );

        while (true) {
            int callId = Interlocked.Increment(ref _nextCallId);
            string path = Path.Combine(
                callLogDirectory,
                $"{callId:0000}.json"
            );
            try {
                var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read
                );
                return new CompletionCallLogReservation(callId, path, stream);
            }
            catch (IOException) when (File.Exists(path)) {
                // Another client or process reserved this numeric id first.
            }
        }
    }

    private static int GetMaxExistingCallId(string callLogDirectory) {
        var max = 0;
        foreach (string path in Directory.EnumerateFiles(
            callLogDirectory,
            "*.json"
        )) {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(
                stem,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int callId
            )) {
                max = Math.Max(max, callId);
            }
        }

        return max;
    }
}

internal sealed class CompletionCallLogReservation(
    int callId,
    string path,
    Stream stream,
    Action<string>? cleanup = null
) : IDisposable {
    private readonly Action<string> _cleanup = cleanup ?? File.Delete;
    private bool _completed;
    private bool _disposed;

    public int CallId { get; } = callId;
    public string Path { get; } = path;
    public Stream Stream { get; } = stream;

    public void Complete() {
        Stream.Flush();
        Stream.Dispose();
        _completed = true;
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        if (_completed) { return; }

        try {
            Stream.Dispose();
        }
        catch {
            // Best-effort cleanup must not replace the original logging failure.
        }

        try {
            _cleanup(Path);
        }
        catch {
            // Best-effort cleanup for an incomplete reserved log.
        }
    }
}

public sealed record CompletionCallLogEntry(
    string Schema,
    int CallId,
    DateTimeOffset TimestampUtc,
    long ElapsedMs,
    CompletionCallLogConnectionSnapshot Connection,
    CompletionCallLogContext Context,
    CompletionCallLogRequest Request,
    CompletionCallLogResponse? Response,
    CompletionCallLogException? Exception
);

public sealed record CompletionCallLogConnectionSnapshot(
    string Id,
    string Kind,
    string ModelId,
    string CompletionSurfaceId,
    string BaseAddress,
    string? BaseAddressEnv,
    string? ApiKeyEnv,
    bool HasApiKey,
    string ProviderId,
    string ApiSpecId,
    int EffectiveRequestTimeoutSeconds
) {
    public static CompletionCallLogConnectionSnapshot From(CompletionConnectionConfig connection, ICompletionClient client) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);

        return new CompletionCallLogConnectionSnapshot(
            connection.Id,
            connection.Kind,
            connection.ModelId,
            connection.CompletionSurfaceId,
            connection.BaseAddress,
            connection.BaseAddressEnv,
            connection.ApiKeyEnv,
            !string.IsNullOrWhiteSpace(connection.ApiKey),
            client.Name,
            client.ApiSpecId,
            DefaultCompletionClientFactory
                .GetEffectiveRequestTimeoutSeconds(connection)
        );
    }
}

public sealed record CompletionCallLogRequest(
    string ModelId,
    string SystemPrompt,
    IReadOnlyList<CompletionCallLogHistoryMessage> Context,
    IReadOnlyList<CompletionCallLogToolDefinition> Tools
) {
    public static CompletionCallLogRequest From(CompletionRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new CompletionCallLogRequest(
            request.ModelId,
            request.SystemPrompt,
            request.Context.Select(CompletionCallLogHistoryMessage.From).ToArray(),
            request.Tools.Select(CompletionCallLogToolDefinition.From).ToArray()
        );
    }
}

public sealed record CompletionCallLogHistoryMessage(
    string Kind,
    string? Content,
    IReadOnlyList<SerializedActionBlock>? ActionBlocks,
    IReadOnlyList<CompletionCallLogToolResult>? ToolResults
) {
    public static CompletionCallLogHistoryMessage From(IHistoryMessage message) {
        ArgumentNullException.ThrowIfNull(message);

        return message switch {
            ActionMessage action => new CompletionCallLogHistoryMessage(
                "action",
                action.GetFlattenedText(),
                ActionMessageSerialization.ToSerializedBlocks(action.Blocks),
                null
            ),
            ToolResultsMessage toolResults => new CompletionCallLogHistoryMessage(
                "tool-results",
                toolResults.Content,
                null,
                toolResults.Results.Select(CompletionCallLogToolResult.From).ToArray()
            ),
            ObservationMessage observation => new CompletionCallLogHistoryMessage("observation", observation.Content, null, null),
            _ => new CompletionCallLogHistoryMessage(message.Kind.ToString(), message.ToString(), null, null)
        };
    }
}

public sealed record CompletionCallLogToolResult(
    string ToolName,
    string ToolCallId,
    ToolExecutionStatus Status,
    string Text
) {
    public static CompletionCallLogToolResult From(ToolResult result) {
        ArgumentNullException.ThrowIfNull(result);
        return new CompletionCallLogToolResult(result.ToolName, result.ToolCallId, result.Status, result.GetFlattenedText());
    }
}

public sealed record CompletionCallLogToolDefinition(
    string Name,
    string Description
) {
    public static CompletionCallLogToolDefinition From(ToolDefinition tool) {
        ArgumentNullException.ThrowIfNull(tool);
        return new CompletionCallLogToolDefinition(tool.Name, tool.Description);
    }
}

public sealed record CompletionCallLogResponse(
    CompletionDescriptor Invocation,
    CompletionTermination Termination,
    IReadOnlyList<string>? Errors,
    string Text,
    IReadOnlyList<SerializedActionBlock> ActionBlocks
) {
    public static CompletionCallLogResponse From(CompletionResult result) {
        ArgumentNullException.ThrowIfNull(result);

        return new CompletionCallLogResponse(
            result.Invocation,
            result.Termination,
            result.Errors,
            result.Message.GetFlattenedText(),
            ActionMessageSerialization.ToSerializedBlocks(result.Message.Blocks)
        );
    }
}

public sealed record CompletionCallLogException(
    string Type,
    string Message,
    string? StackTrace
) {
    public static CompletionCallLogException From(Exception exception) {
        ArgumentNullException.ThrowIfNull(exception);
        return new CompletionCallLogException(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.StackTrace);
    }
}
