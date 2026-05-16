using System.Collections.Immutable;

namespace Atelia.Completion.Tools;

internal sealed record ToolArgumentParsingResult(
    ImmutableDictionary<string, object?> Arguments,
    ImmutableDictionary<string, string> RawArguments,
    string? ParseError,
    string? ParseWarning
);
