using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;

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
    private readonly string _callLogDir;
    private readonly CompletionCallLogContext _context;
    private int _nextCallId;

    public LoggingCompletionClient(
        ICompletionClient inner,
        CompletionConnectionConfig connection,
        string callLogDir,
        CompletionCallLogContext? context = null
    ) {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _callLogDir = string.IsNullOrWhiteSpace(callLogDir) ? throw new ArgumentException("Call log directory must not be blank.", nameof(callLogDir)) : Path.GetFullPath(callLogDir);
        _context = context ?? new CompletionCallLogContext();
        Directory.CreateDirectory(_callLogDir);
        _nextCallId = GetMaxExistingCallId(_callLogDir);
    }

    public string Name => _inner.Name;

    public string ApiSpecId => _inner.ApiSpecId;

    public async Task<CompletionResult> StreamCompletionAsync(
        CompletionRequest request,
        CompletionStreamObserver? observer,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        int callId = Interlocked.Increment(ref _nextCallId);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        CompletionResult result;

        try {
            result = await _inner.StreamCompletionAsync(request, observer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) {
            stopwatch.Stop();
            try {
                WriteCallLog(callId, startedAt, stopwatch.Elapsed, request, result: null, ex);
            }
            catch (Exception logException) {
                throw new AggregateException("Completion call failed and writing the call log also failed.", ex, logException);
            }
            throw;
        }

        stopwatch.Stop();
        WriteCallLog(callId, startedAt, stopwatch.Elapsed, request, result, exception: null);
        return result;
    }

    private static int GetMaxExistingCallId(string callLogDir) {
        var max = 0;
        foreach (var path in Directory.EnumerateFiles(callLogDir, "*.json")) {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out int callId)) {
                max = Math.Max(max, callId);
            }
        }

        return max;
    }

    private void WriteCallLog(
        int callId,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        CompletionRequest request,
        CompletionResult? result,
        Exception? exception
    ) {
        var log = new CompletionCallLogEntry(
            Schema: "atelia.completion.call-log.v1",
            CallId: callId,
            TimestampUtc: startedAt,
            ElapsedMs: (long)elapsed.TotalMilliseconds,
            Connection: CompletionCallLogConnectionSnapshot.From(_connection, _inner),
            Context: _context,
            Request: CompletionCallLogRequest.From(request),
            Response: result is null ? null : CompletionCallLogResponse.From(result),
            Exception: exception is null ? null : CompletionCallLogException.From(exception)
        );

        string path = Path.Combine(_callLogDir, $"{callId:0000}.json");
        string json = JsonSerializer.Serialize(log, JsonOptions);
        File.WriteAllText(path, json);
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
    string ApiSpecId
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
            client.ApiSpecId
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
