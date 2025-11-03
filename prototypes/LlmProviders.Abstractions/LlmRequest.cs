using System.Collections.Generic;
using System.Collections.Immutable;

namespace Atelia.LlmProviders;

public sealed record LlmRequest(
    string ModelId,
    string SystemInstruction,
    IReadOnlyList<IContextMessage> Context,
    ImmutableArray<ToolDefinition> Tools
);
