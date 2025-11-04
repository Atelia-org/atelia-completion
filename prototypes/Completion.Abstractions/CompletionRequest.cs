using System.Collections.Generic;
using System.Collections.Immutable;

namespace Atelia.Completion.Abstractions;

public sealed record CompletionRequest(
    string ModelId,
    string SystemPrompt,
    IReadOnlyList<IContextMessage> Context,
    ImmutableArray<ToolDefinition> Tools
);
