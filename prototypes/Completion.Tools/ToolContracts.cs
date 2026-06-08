using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

public static class ToolContracts {
    public static ToolDefinition GetValidatedDefinition(ITool tool) {
        if (tool is null) { throw new ArgumentNullException(nameof(tool)); }
        if (tool.Definition is null) { throw new InvalidOperationException($"Tool '{tool.GetType().FullName}' returned null Definition."); }
        return tool.Definition;
    }
}
