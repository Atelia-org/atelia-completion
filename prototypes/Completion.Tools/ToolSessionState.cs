using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

public sealed class ToolSessionState {
    private long _nextExecutionSequence;

    public ToolSessionState(
        ToolAccessPolicy? toolAccess = null,
        IServiceProvider? services = null,
        IReadOnlyDictionary<string, object?>? items = null
    ) {
        ToolAccess = toolAccess ?? ToolAccessPolicy.AllowAll;
        Services = services;
        Items = items;
    }

    public ToolAccessPolicy ToolAccess { get; }

    public IServiceProvider? Services { get; }

    public IReadOnlyDictionary<string, object?>? Items { get; }

    public ImmutableArray<ToolDefinition> GetVisibleToolDefinitions(ToolRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);

        if (!registry.Tools.Any()) { return ImmutableArray<ToolDefinition>.Empty; }

        var builder = ImmutableArray.CreateBuilder<ToolDefinition>();
        foreach (var tool in registry.Tools) {
            if (!ToolAccess.IsVisible(tool.Name)) { continue; }
            builder.Add(tool.Definition);
        }

        return builder.Count == 0 ? ImmutableArray<ToolDefinition>.Empty : builder.ToImmutable();
    }

    internal long AllocateExecutionSequence() => Interlocked.Increment(ref _nextExecutionSequence);
}
