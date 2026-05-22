using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Completion.Tools;

public sealed class ToolExecutor {
    private const string DebugCategory = "Tools";
    private readonly ToolRegistry _registry;
    private readonly ToolSessionState _session;

    public ToolExecutor(ToolRegistry registry, ToolSessionState session) {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        DebugUtil.Info(DebugCategory, $"ToolExecutor initialized toolCount={_registry.AllDefinitions.Length}");
    }

    public ToolRegistry Registry => _registry;

    public ToolSessionState Session => _session;

    public ImmutableArray<ToolDefinition> VisibleToolDefinitions => _session.GetVisibleToolDefinitions(_registry);

    public bool TryGetTool(string name, out ITool tool) {
        if (string.IsNullOrWhiteSpace(name)) {
            tool = null!;
            return false;
        }

        if (!_session.ToolAccess.IsExecutable(name)) {
            tool = null!;
            return false;
        }

        if (_registry.TryGet(name, out var registeredTool)) {
            tool = registeredTool.Tool;
            return true;
        }

        tool = null!;
        return false;
    }

    public async ValueTask<ToolCallExecutionResult> ExecuteAsync(
        RawToolCall request,
        CancellationToken cancellationToken
    ) {
        var executionSequence = _session.AllocateExecutionSequence();
        var toolExecutionRequest = new ToolExecutionContext(_session, request, executionSequence);

        DebugUtil.Info(DebugCategory, $"[Executor] Dispatch toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence}");

        if (!_session.ToolAccess.IsExecutable(request.ToolName)) {
            DebugUtil.Warning(DebugCategory, $"[Executor] Forbidden toolName={request.ToolName} executionSequence={executionSequence}");

            var message = $"当前 session 不允许执行工具: {request.ToolName}";
            return new ToolCallExecutionResult(
                new ToolExecuteResult(ToolExecutionStatus.Failed, message),
                request.ToolName,
                request.ToolCallId
            );
        }

        if (!_registry.TryGet(request.ToolName, out var registeredTool)) {
            DebugUtil.Warning(DebugCategory, $"[Executor] Missing tool toolName={request.ToolName} executionSequence={executionSequence}");

            var message = $"未找到工具: {request.ToolName}";
            return new ToolCallExecutionResult(
                new ToolExecuteResult(ToolExecutionStatus.Failed, message),
                request.ToolName,
                request.ToolCallId
            );
        }

        var stopwatch = Stopwatch.StartNew();

        try {
            var executeResult = await registeredTool.Tool.ExecuteAsync(toolExecutionRequest, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tool '{registeredTool.Name}' returned null result.");
            stopwatch.Stop();

            DebugUtil.Info(
                DebugCategory,
                $"[Executor] Completed toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence} status={executeResult.Status} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}"
            );

            return new ToolCallExecutionResult(executeResult, request.ToolName, request.ToolCallId, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) {
            stopwatch.Stop();
            DebugUtil.Warning(DebugCategory, $"[Executor] Cancelled toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence}");

            return new ToolCallExecutionResult(
                new ToolExecuteResult(ToolExecutionStatus.Skipped, "工具执行被取消"),
                request.ToolName,
                request.ToolCallId,
                stopwatch.Elapsed
            );
        }
        catch (Exception ex) {
            stopwatch.Stop();
            DebugUtil.Error(DebugCategory, $"[Executor] Failed toolName={request.ToolName} toolCallId={request.ToolCallId} executionSequence={executionSequence} error={ex.Message}", ex);

            var message = $"工具执行异常: {ex.Message}";
            return new ToolCallExecutionResult(
                new ToolExecuteResult(ToolExecutionStatus.Failed, message),
                request.ToolName,
                request.ToolCallId,
                stopwatch.Elapsed
            );
        }
    }
}
