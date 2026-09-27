using Atelia.Completion.Abstractions;

namespace Atelia.Completion;

/// <summary>Rejects unsupported raw-text inputs before any provider dispatch.</summary>
internal static class CompletionToolInputValidation {
    public static void RequireJsonTools(CompletionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PromptPrefix.OutputContract.Tools.Any(static tool => tool.InputKind == ToolInputKind.Text)
            || HasTextCalls(request.PromptPrefix.SharedContextMessages)
            || HasTextCalls(request.TailMessages)) {
            throw new CompletionRequestRejectedException(
                CompletionTermination.Failed(
                    "tool-input.unsupported-text",
                    "This provider adapter does not support raw-text tool definitions or history calls; the request was rejected before dispatch."
                ),
                ["adapter-validation=tool-input-kind"]
            );
        }
    }

    private static bool HasTextCalls(IEnumerable<IHistoryMessage> messages) => messages
        .OfType<ActionMessage>()
        .SelectMany(static message => message.Blocks)
        .OfType<ActionBlock.ToolCall>()
        .Any(static block => block.Call.InputKind == ToolInputKind.Text);
}
