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
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var executionSequence = session.AllocateExecutionSequence();
        var context = new ToolExecutionContext(session, request, executionSequence);

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
        catch (OperationCanceledException) {
            stopwatch.Stop();
            DebugUtil.Warning(DebugCategory, $"[Dispatch] Cancelled toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence}");

            return new ToolCallExecutionResult(
                request,
                ToolExecuteResult.FromText(ToolExecutionStatus.Skipped, "工具执行被取消"),
                stopwatch.Elapsed
            );
        }
        catch (Exception ex) {
            stopwatch.Stop();
            DebugUtil.Error(DebugCategory, $"[Dispatch] Failed toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence} error={ex.Message}", ex);

            var message = $"工具执行异常: {ex.Message}";
            return new ToolCallExecutionResult(
                request,
                ToolExecuteResult.FromText(ToolExecutionStatus.Failed, message),
                stopwatch.Elapsed
            );
        }
    }
}
