using System.Collections.Immutable;

namespace Atelia.LlmProviders.Utils;

internal sealed record ToolArgumentParsingResult(
    ImmutableDictionary<string, object?> Arguments,
    ImmutableDictionary<string, string> RawArguments,
    string? ParseError,
    string? ParseWarning
);
