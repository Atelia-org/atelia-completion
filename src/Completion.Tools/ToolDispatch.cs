using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Completion.Tools;

/// <summary>
/// 无状态工具调度器：承担授权校验、分发、日志、异常治理与耗时统计。
/// </summary>
/// <remarks>
/// 它由 <see cref="ToolSession.ExecuteAsync"/> 内化调用，<b>不作为使用方一等公开概念</b>。
/// 保留为 internal 静态形态，是为「无状态 dispatcher + 纯数据 session」这条未来路线预留的逃生口。
/// </remarks>
internal static class ToolDispatch {
    private const string DebugCategory = "Tools";

    public static async ValueTask<ToolCallExecutionResult> ExecuteAsync(
        ToolSession session,
        RawToolCall request,
        long executionSequence,
        string? operationId,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var context = new ToolExecutionContext(
            session,
            request,
            executionSequence,
            operationId
        );

        DebugUtil.Info(DebugCategory, $"[Dispatch] toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence}");

        if (!session.Access.IsExecutable(request.ToolName)) {
            DebugUtil.Warning(DebugCategory, $"[Dispatch] Forbidden toolName={request.ToolName} executionSequence={executionSequence}");

            var message = $"当前 session 不允许执行工具: {request.ToolName}";
            return new ToolCallExecutionResult(
                request,
                ToolExecuteResult.FromText(ToolExecutionStatus.Failed, message)
            );
        }

        if (!session.Registry.TryGet(request.ToolName, out var registeredTool)) {
            DebugUtil.Warning(DebugCategory, $"[Dispatch] Missing tool toolName={request.ToolName} executionSequence={executionSequence}");

            var message = $"未找到工具: {request.ToolName}";
            return new ToolCallExecutionResult(
                request,
                ToolExecuteResult.FromText(ToolExecutionStatus.Failed, message)
            );
        }

        var stopwatch = Stopwatch.StartNew();

        try {
            var executeResult = await registeredTool.Tool.ExecuteAsync(context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tool '{registeredTool.Name}' returned null result.");
            stopwatch.Stop();

            DebugUtil.Info(
                DebugCategory,
                $"[Dispatch] Completed toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence} status={executeResult.Status} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}"
            );

            return new ToolCallExecutionResult(request, executeResult, stopwatch.Elapsed);
        }
        catch (ToolExecutionCancelledBeforeMutationException)
            when (cancellationToken.IsCancellationRequested) {
            stopwatch.Stop();
            DebugUtil.Warning(DebugCategory, $"[Dispatch] Cancelled toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence}");

            return new ToolCallExecutionResult(
                request,
                ToolExecuteResult.FromText(ToolExecutionStatus.Skipped, "工具执行被取消"),
                stopwatch.Elapsed
            );
        }
        catch (ToolExecutionUnsettledException) {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex) when (IsFatal(ex)) {
            stopwatch.Stop();
            throw;
        }
        catch (OperationCanceledException) {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex) {
            stopwatch.Stop();
            DebugUtil.Error(DebugCategory, $"[Dispatch] Failed toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence} error={BoundDetail(ex.Message)}", ex);

            var message = $"工具执行异常: {BoundDetail(ex.Message)}";
            return new ToolCallExecutionResult(
                request,
                ToolExecuteResult.FromText(ToolExecutionStatus.Failed, message),
                stopwatch.Elapsed
            );
        }
    }

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    private static string BoundDetail(string? detail) {
        const int maximumUtf8Bytes = 4 * 1024;
        if (string.IsNullOrEmpty(detail)) {
            return "No detail was provided.";
        }
        var encoding = new System.Text.UTF8Encoding(false, true);
        try {
            if (encoding.GetByteCount(detail) <= maximumUtf8Bytes) {
                return detail;
            }
        }
        catch (System.Text.EncoderFallbackException) {
            return "The exception detail was not strict UTF-8 text.";
        }
        var builder = new System.Text.StringBuilder(
            Math.Min(detail.Length, maximumUtf8Bytes)
        );
        int bytes = 0;
        foreach (System.Text.Rune rune in detail.EnumerateRunes()) {
            if (bytes + rune.Utf8SequenceLength > maximumUtf8Bytes) {
                break;
            }
            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }
}
