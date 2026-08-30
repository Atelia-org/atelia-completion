using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

public sealed class ToolRegistry {
    private readonly IReadOnlyDictionary<string, RegisteredTool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools) {
        ArgumentNullException.ThrowIfNull(tools);

        var dictionary = new Dictionary<string, RegisteredTool>(StringComparer.OrdinalIgnoreCase);
        var definitionBuilder = ImmutableArray.CreateBuilder<ToolDefinition>();

        foreach (var tool in tools) {
            if (tool is null) { continue; }

            var definition = ToolContracts.GetValidatedDefinition(tool);
            if (dictionary.ContainsKey(definition.Name)) { throw new InvalidOperationException($"Duplicate tool registration detected for '{definition.Name}'."); }

            var registeredTool = new RegisteredTool(definition.Name, definition, tool);
            dictionary[definition.Name] = registeredTool;
            definitionBuilder.Add(definition);
        }

        _tools = dictionary;
        AllDefinitions = definitionBuilder.Count == 0
            ? ImmutableArray<ToolDefinition>.Empty
            : definitionBuilder.ToImmutable();
    }

    public IEnumerable<RegisteredTool> Tools => _tools.Values;

    public ImmutableArray<ToolDefinition> AllDefinitions { get; }

    public bool TryGet(string toolName, out RegisteredTool tool) {
        if (string.IsNullOrWhiteSpace(toolName)) {
            tool = null!;
            return false;
        }

        return _tools.TryGetValue(toolName, out tool!);
    }

    /// <summary>
    /// 产出一个绑定到本注册表的 <see cref="ToolSession"/>——使用方与工具系统交互的唯一公开装配入口。
    /// </summary>
    public ToolSession CreateSession(
        ToolAccessSnapshot? access = null,
        IServiceProvider? services = null,
        IReadOnlyDictionary<string, object?>? items = null
    ) => new(this, access ?? ToolAccessSnapshot.AllowAll, services, items);
}

public sealed record RegisteredTool(
    string Name,
    ToolDefinition Definition,
    ITool Tool
);
