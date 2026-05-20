using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

namespace Atelia.Completion.Tools;

internal static class ToolDefinitionBuilder {
    public static ToolDefinition FromTool(ITool tool) {
        if (tool is null) { throw new ArgumentNullException(nameof(tool)); }

        var parameters = tool.Parameters is { Count: > 0 }
            ? ImmutableArray.CreateRange(tool.Parameters)
            : ImmutableArray<ToolParamSpec>.Empty;

        return ToolDefinition.CreateFlat(tool.Name, tool.Description, parameters);
    }
}
