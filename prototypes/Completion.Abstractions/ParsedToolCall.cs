namespace Atelia.Completion.Abstractions;

/// <summary>
/// Captures a single tool invocation emitted by a provider.
/// </summary>
/// <remarks>
/// <para>
/// Providers are expected to populate <paramref name="Arguments"/> with strongly-typed values when parsing succeeds.
/// Whenever parsing of a specific parameter fails, the original textual representation should still be present in
/// <paramref name="RawArguments"/> so that other providers or diagnostics can faithfully reconstruct the call.
/// </para>
/// <para>
/// If the provider cannot even determine the parameter key/value pairs (for example the payload is not an object),
/// <paramref name="RawArguments"/> should be set to <see langword="null"/> and <paramref name="ParseError"/> must explain the failure.
/// </para>
/// </remarks>
/// <param name="ToolName">Logical tool identifier selected by the model.</param>
/// <param name="ToolCallId">Provider specific identifier used to correlate execution results.</param>
/// <param name="RawArguments">
/// Canonical textual snapshot of arguments keyed by parameter name. Values should keep the original string that came
/// from the model (without additional interpretation) so downstream components can recover or re-serialize them.
/// </param>
/// <param name="Arguments">Parsed arguments ready for execution. May be <see langword="null"/> when parsing failed.</param>
/// <param name="ParseError">Fatal parse errors (null when parsing succeeded).</param>
/// <param name="ParseWarning">Non-fatal issues detected during parsing (null when none).</param>
public record ParsedToolCall(
    string ToolName,
    string ToolCallId,
    IReadOnlyDictionary<string, string>? RawArguments,
    IReadOnlyDictionary<string, object?>? Arguments,
    string? ParseError,
    string? ParseWarning
);

public enum ToolExecutionStatus {
    Success,
    Failed,
    Skipped
}

public record ToolResult(
    string ToolName,
    string ToolCallId,
    ToolExecutionStatus Status,
    string Result,
    TimeSpan? Elapsed
);
