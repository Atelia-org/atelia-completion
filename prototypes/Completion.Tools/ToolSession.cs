using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Completion.Tools;

/// <summary>
/// 某个 LLM 会话的工具运行态：使用方唯一需要持有的有状态主概念。
/// 由 <see cref="ToolRegistry.CreateSession"/> 工厂产出，并内化了工具调度（见 <see cref="ExecuteAsync"/>）。
/// </summary>
/// <remarks>
/// 线程约定：本类型面向单线程顺序使用的 agent / 会话 loop，<b>非线程安全</b>。
/// <para>
/// <see cref="Registry"/> 与 <see cref="Access"/> 是可变属性：会话期间工具集可能变化（rebind
/// <see cref="Registry"/>）、对模型可见性可能逐轮替换（设置 <see cref="Access"/>）。而执行序号
/// （<see cref="ToolExecutionContext.ExecutionSequence"/> 的来源）在整个会话内单调递增，<b>不随上述变化重置</b>，
/// 因此可稳定用于去重、审计与日志关联。
/// </para>
/// </remarks>
public sealed class ToolSession {
    private const string DebugCategory = "Tools";

    private ToolRegistry _registry;
    private ToolAccessSnapshot _access;
    private long _nextExecutionSequence;

    internal ToolSession(
        ToolRegistry registry,
        ToolAccessSnapshot access,
        IServiceProvider? services,
        IReadOnlyDictionary<string, object?>? items
    ) {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        Services = services;
        Items = items;
        DebugUtil.Trace(DebugCategory, $"ToolSession created toolCount={registry.AllDefinitions.Length}");
    }

    /// <summary>
    /// 当前绑定的工具注册表。可在工具集动态变化时 rebind，<b>不会重置执行序号</b>。
    /// </summary>
    public ToolRegistry Registry {
        get => _registry;
        set => _registry = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// 当前会话对模型的访问快照。可逐轮替换，<b>不会重置执行序号</b>。
    /// </summary>
    public ToolAccessSnapshot Access {
        get => _access;
        set => _access = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IServiceProvider? Services { get; }

    public IReadOnlyDictionary<string, object?>? Items { get; }

    /// <summary>
    /// 当前对模型可见的工具定义，是 <c>Registry × Access</c> 的交叉派生。
    /// join 归属于 session，使用方无需再把 registry 传回去。
    /// </summary>
    public ImmutableArray<ToolDefinition> VisibleDefinitions {
        get {
            var registry = _registry;
            var access = _access;
            var builder = ImmutableArray.CreateBuilder<ToolDefinition>();
            foreach (var tool in registry.Tools) {
                if (!access.IsVisible(tool.Name)) { continue; }
                builder.Add(tool.Definition);
            }

            return builder.Count == 0 ? ImmutableArray<ToolDefinition>.Empty : builder.ToImmutable();
        }
    }

    /// <summary>
    /// 在当前 access 授权下解析可执行工具实例；不可见 / 不可执行 / 未注册均返回 <c>false</c>。
    /// </summary>
    public bool TryGetTool(string name, out ITool tool) {
        if (string.IsNullOrWhiteSpace(name) || !_access.IsExecutable(name)) {
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

    /// <summary>
    /// 执行一次工具调用。授权校验、分发、日志、异常治理与耗时统计由内化的
    /// <see cref="ToolDispatch"/> 承担。
    /// </summary>
    public ValueTask<ToolCallExecutionResult> ExecuteAsync(RawToolCall request, CancellationToken cancellationToken)
        => ToolDispatch.ExecuteAsync(this, request, cancellationToken);

    internal long AllocateExecutionSequence() => Interlocked.Increment(ref _nextExecutionSequence);
}
