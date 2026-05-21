using System.Collections.Immutable;
using System.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Completion.Tools;

public sealed class ToolExecutor {
    private const string DebugCategory = "Tools";
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly ImmutableArray<ToolDefinition> _allToolDefinitions;
    private readonly Dictionary<ITool, ToolDefinition> _definitionByInstance;

    public ToolExecutor(IEnumerable<ITool> tools) {
        if (tools is null) { throw new ArgumentNullException(nameof(tools)); }

        var dictionary = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools) {
            if (tool is null) { continue; }

            var definition = ToolContracts.GetValidatedDefinition(tool);
            if (dictionary.ContainsKey(definition.Name)) {
                throw new InvalidOperationException($"Duplicate tool registration detected for '{definition.Name}'.");
            }

            dictionary[definition.Name] = tool;
        }

        _tools = dictionary;

        var definitionMap = new Dictionary<ITool, ToolDefinition>(ReferenceEqualityComparer.Instance);
        var definitionBuilder = ImmutableArray.CreateBuilder<ToolDefinition>(_tools.Count);

        foreach (var tool in _tools.Values) {
            if (tool is null) { continue; }

            var definition = tool.Definition;
            definitionMap[tool] = definition;
            definitionBuilder.Add(definition);
        }

        _definitionByInstance = definitionMap;
        _allToolDefinitions = definitionBuilder.Count == 0
            ? ImmutableArray<ToolDefinition>.Empty
            : definitionBuilder.ToImmutable();
        DebugUtil.Info(DebugCategory, $"ToolExecutor initialized toolCount={_tools.Count}");
    }

    public IEnumerable<ITool> Tools => _tools.Values;

    public ImmutableArray<ToolDefinition> AllToolDefinitions => _allToolDefinitions;

    public ImmutableArray<ToolDefinition> GetVisibleToolDefinitions() {
        if (_tools.Count == 0) { return ImmutableArray<ToolDefinition>.Empty; }

        var builder = ImmutableArray.CreateBuilder<ToolDefinition>();
        foreach (var tool in _tools.Values) {
            if (tool is null || !tool.Visible) { continue; }

            if (_definitionByInstance.TryGetValue(tool, out var definition)) {
                builder.Add(definition);
            }
        }

        return builder.Count == 0 ? ImmutableArray<ToolDefinition>.Empty : builder.ToImmutable();
    }

    public bool TryGetTool(string name, out ITool tool) {
        if (string.IsNullOrWhiteSpace(name)) {
            tool = null!;
            return false;
        }

        return _tools.TryGetValue(name, out tool!);
    }

    public async ValueTask<ToolCallExecutionResult> ExecuteAsync(
        RawToolCall request,
        CancellationToken cancellationToken
    ) {
        DebugUtil.Info(DebugCategory, $"[Executor] Dispatch toolName={request.ToolName} toolCallId={request.ToolCallId}");

        if (!_tools.TryGetValue(request.ToolName, out var tool)) {
            DebugUtil.Warning(DebugCategory, $"[Executor] Missing tool toolName={request.ToolName}");

            var message = $"未找到工具: {request.ToolName}";
            return new ToolCallExecutionResult(
                new ToolExecuteResult(ToolExecutionStatus.Failed, message),
                request.ToolName,
                request.ToolCallId
            );
        }

        var stopwatch = Stopwatch.StartNew();
        var resolvedRequest = ResolveToolCall(request, tool);

        if (!string.IsNullOrWhiteSpace(resolvedRequest.ParseError)) {
            stopwatch.Stop();
            DebugUtil.Warning(
                DebugCategory,
                $"[Executor] Argument parse failed toolName={request.ToolName} toolCallId={request.ToolCallId} error={resolvedRequest.ParseError}"
            );

            return CreateParseFailureResult(request, resolvedRequest);
        }

        try {
            var executeResult = await tool.ExecuteAsync(resolvedRequest.Arguments, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tool '{tool.Name}' returned null result.");
            stopwatch.Stop();

            DebugUtil.Info(
                DebugCategory,
                $"[Executor] Completed toolName={request.ToolName} toolCallId={request.ToolCallId} status={executeResult.Status} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}"
            );

            var enrichedResult = AttachParseWarning(executeResult, resolvedRequest.ParseWarning);
            return new ToolCallExecutionResult(enrichedResult, request.ToolName, request.ToolCallId, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) {
            stopwatch.Stop();
            DebugUtil.Warning(DebugCategory, $"[Executor] Cancelled toolName={request.ToolName} toolCallId={request.ToolCallId}");

            return new ToolCallExecutionResult(
                new ToolExecuteResult(ToolExecutionStatus.Skipped, "工具执行被取消"),
                request.ToolName,
                request.ToolCallId,
                stopwatch.Elapsed
            );
        }
        catch (Exception ex) {
            stopwatch.Stop();
            DebugUtil.Error(DebugCategory, $"[Executor] Failed toolName={request.ToolName} toolCallId={request.ToolCallId} error={ex.Message}", ex);

            var message = $"工具执行异常: {ex.Message}";
            return new ToolCallExecutionResult(
                new ToolExecuteResult(ToolExecutionStatus.Failed, message),
                request.ToolName,
                request.ToolCallId,
                stopwatch.Elapsed
            );
        }
    }

    private ResolvedToolCall ResolveToolCall(RawToolCall request, ITool tool) {
        var definition = _definitionByInstance[tool];
        var parsed = JsonArgumentParser.ParseArguments(definition.Parameters, request.RawArgumentsJson);
        return new ResolvedToolCall(
            request.ToolName,
            request.ToolCallId,
            parsed.Arguments,
            parsed.ParseError,
            parsed.ParseWarning
        );
    }

    private static ToolCallExecutionResult CreateParseFailureResult(RawToolCall request, ResolvedToolCall resolvedRequest) {
        var content = "工具参数解析失败。";

        if (!string.IsNullOrWhiteSpace(resolvedRequest.ParseError)) {
            content = string.Concat(content, "\n解析错误: ", resolvedRequest.ParseError);
        }

        if (!string.IsNullOrWhiteSpace(request.RawArgumentsJson)) {
            content = string.Concat(content, "\nraw_arguments_json: ", request.RawArgumentsJson);
        }

        return new ToolCallExecutionResult(
            new ToolExecuteResult(ToolExecutionStatus.Failed, content),
            request.ToolName,
            request.ToolCallId
        );
    }

    private static ToolExecuteResult AttachParseWarning(ToolExecuteResult content, string? parseWarning) {
        if (string.IsNullOrWhiteSpace(parseWarning)) { return content; }

        var mergedContent = string.Concat(content.Content, "\n[ParseWarning] ", parseWarning);
        return new ToolExecuteResult(content.Status, mergedContent);
    }
}
