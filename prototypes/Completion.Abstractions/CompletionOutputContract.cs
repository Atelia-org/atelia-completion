using System.Collections.Immutable;

namespace Atelia.Completion.Abstractions;

/// <summary>
/// Provider-neutral policy controlling whether and how a model may call tools.
/// </summary>
public enum CompletionToolChoiceKind {
    ProviderDefault,
    Auto,
    None,
    RequiredAny,
    RequiredNamed,
}

/// <summary>
/// Closed provider-neutral tool-choice union.
/// </summary>
public sealed record CompletionToolChoice {
    private CompletionToolChoice(
        CompletionToolChoiceKind kind,
        string? requiredToolName
    ) {
        Kind = kind;
        RequiredToolName = requiredToolName;
    }

    public CompletionToolChoiceKind Kind { get; }

    public string? RequiredToolName { get; }

    public static CompletionToolChoice ProviderDefault { get; } = new(
        CompletionToolChoiceKind.ProviderDefault,
        requiredToolName: null
    );

    public static CompletionToolChoice Auto { get; } = new(
        CompletionToolChoiceKind.Auto,
        requiredToolName: null
    );

    public static CompletionToolChoice None { get; } = new(
        CompletionToolChoiceKind.None,
        requiredToolName: null
    );

    public static CompletionToolChoice RequiredAny { get; } = new(
        CompletionToolChoiceKind.RequiredAny,
        requiredToolName: null
    );

    public static CompletionToolChoice RequiredNamed(string toolName) => new(
        CompletionToolChoiceKind.RequiredNamed,
        string.IsNullOrWhiteSpace(toolName)
            ? throw new ArgumentException(
                "Required tool name cannot be empty.",
                nameof(toolName)
            )
            : toolName
    );
}

/// <summary>
/// Immutable provider-facing output protocol shared by a stable prompt prefix.
/// </summary>
public sealed class CompletionOutputContract {
    public CompletionOutputContract(
        ImmutableArray<ToolDefinition> tools,
        CompletionToolChoice toolChoice,
        bool? allowParallelToolCalls = null
    ) {
        Tools = tools.IsDefault
            ? ImmutableArray<ToolDefinition>.Empty
            : tools;
        if (Tools.Any(static tool => tool is null)) {
            throw new ArgumentException(
                "Tool definitions cannot contain null elements.",
                nameof(tools)
            );
        }

        ToolChoice = toolChoice
            ?? throw new ArgumentNullException(nameof(toolChoice));
        AllowParallelToolCalls = allowParallelToolCalls;

        if (ToolChoice.Kind is CompletionToolChoiceKind.RequiredAny
            && Tools.IsEmpty) {
            throw new ArgumentException(
                "RequiredAny tool choice requires at least one tool definition.",
                nameof(toolChoice)
            );
        }

        if (ToolChoice.Kind is CompletionToolChoiceKind.RequiredNamed) {
            string requiredToolName = ToolChoice.RequiredToolName
                ?? throw new ArgumentException(
                    "RequiredNamed tool choice must carry a tool name.",
                    nameof(toolChoice)
                );
            if (!Tools.Any(tool => string.Equals(
                    tool.Name,
                    requiredToolName,
                    StringComparison.Ordinal
                ))) {
                throw new ArgumentException(
                    $"Required tool '{requiredToolName}' is absent from the ordered tool set.",
                    nameof(toolChoice)
                );
            }
        }
    }

    public ImmutableArray<ToolDefinition> Tools { get; }

    public CompletionToolChoice ToolChoice { get; }

    public bool? AllowParallelToolCalls { get; }

    public bool IsProviderDefault
        => ToolChoice.Kind is CompletionToolChoiceKind.ProviderDefault
            && AllowParallelToolCalls is null;

    public static CompletionOutputContract ProviderDefault(
        ImmutableArray<ToolDefinition> tools
    ) => new(
        tools,
        CompletionToolChoice.ProviderDefault,
        allowParallelToolCalls: null
    );
}
