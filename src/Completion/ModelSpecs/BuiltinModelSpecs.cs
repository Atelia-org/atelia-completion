using Atelia.Completion.Abstractions;

namespace Atelia.Completion.ModelSpecs;

/// <summary>
/// Reviewed default knowledge for specific protocol surfaces. These catalogs contain
/// exact IDs only; hosts explicitly extend or replace them for gateways and aliases.
/// </summary>
public static class BuiltinModelSpecs {
    // Reviewed 2026-10-01: https://docs.z.ai/guides/capabilities/thinking
    // See docs/Completion/specs/models/glm-5.3.md. Standard Chat API: low/high/max,
    // mandatory thinking. Rounding is library policy rather than provider normalization.
    public static CompletionModelSpecCatalog StandardChat { get; } =
        CompletionModelSpecCatalog.Empty.WithModel("glm-5.3", new() {
            ReasoningMapper = ReasoningEffortMappers.ForSupportedLevels([
                CompletionReasoningEffort.Low, CompletionReasoningEffort.High,
                CompletionReasoningEffort.Max
            ])
        });

    // Reviewed 2026-10-01: https://api-docs.deepseek.com/api/create-chat-completion/
    // See docs/Completion/specs/models/deepseek-v4-1-flash.md. Native DeepSeek Chat:
    // none/low/high/max. No filename-derived aliases.
    public static CompletionModelSpecCatalog DeepSeekChat { get; } =
        CompletionModelSpecCatalog.Empty.WithModel("deepseek-flash", new() {
            ReasoningMapper = ReasoningEffortMappers.ForSupportedLevels([
                CompletionReasoningEffort.Low, CompletionReasoningEffort.High,
                CompletionReasoningEffort.Max
            ], supportsDisabled: true)
        });

    public static CompletionModelSpecCatalog AnthropicMessages { get; } = CreateAnthropicMessages();

    public static CompletionModelSpecCatalog GeminiGenerateContent { get; } = CreateGeminiGenerateContent();

    private static CompletionModelSpecCatalog CreateGeminiGenerateContent() {
        // Reviewed 2026-10-01: https://ai.google.dev/gemini-api/docs/generate-content/thinking
        // See docs/Completion/specs/models/index.md and gemini-generate-content.md.
        // Six stable text models and current Pro Preview all support low/medium/high.
        // The shared subset deliberately excludes minimal; Disabled -> Low is library
        // policy, not a promise to turn thinking off. ProviderDefault omits the control.
        var spec = new CompletionModelSpec {
            OutputTokenLimit = 65_536,
            ReasoningMapper = ReasoningEffortMappers.ForSupportedLevels([
                CompletionReasoningEffort.Low, CompletionReasoningEffort.Medium,
                CompletionReasoningEffort.High
            ])
        };
        var catalog = CompletionModelSpecCatalog.Empty;
        foreach (string modelId in new[] {
            "gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash",
            "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-3.1-flash-lite",
            "gemini-3.1-pro-preview"
        }) {
            catalog = catalog.WithModels([modelId, $"models/{modelId}"], spec);
        }
        return catalog;
    }

    private static CompletionModelSpecCatalog CreateAnthropicMessages() {
        // Migrate the existing exact-ID maxima; standard Messages streaming, not Batch beta.
        // https://platform.claude.com/docs/en/models/overview (reviewed 2026-10-01).
        // See docs/Completion/model-specs-design.md for exact-ID migration scope.
        var catalog = CompletionModelSpecCatalog.Empty.WithModels([
            "claude-opus-4-6", "claude-opus-4-7", "claude-opus-4-8", "claude-opus-5"
        ], new() { OutputTokenLimit = 128_000 });

        // https://platform.claude.com/docs/en/build-with-claude/effort
        // https://platform.claude.com/docs/en/build-with-claude/thinking
        // See docs/Completion/specs/models/claude-opus-5-5.md.
        // Reviewed 2026-10-01: Opus 5.5 has five effort levels, always-on adaptive
        // thinking and no forced tool selection. Do not infer this for the whole family.
        return catalog.WithModel("claude-opus-5-5", new() {
            OutputTokenLimit = 128_000,
            ReasoningMapper = ReasoningEffortMappers.ForSupportedLevels([
                CompletionReasoningEffort.Low, CompletionReasoningEffort.Medium,
                CompletionReasoningEffort.High, CompletionReasoningEffort.XHigh,
                CompletionReasoningEffort.Max
            ]),
            ForcedToolChoiceSupported = false
        });
    }
}
