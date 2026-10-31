namespace Atelia.Completion.Abstractions;

/// <summary>
/// Provider-neutral hint describing when the stable prompt prefix is expected to
/// be reused. Providers map the hint to the closest supported cache behavior.
/// </summary>
/// <remarks>
/// This is an economic reuse hint, not a privacy or data-retention guarantee.
/// A provider may accept the hint as an explicit no-op when its selected API
/// surface does not expose a matching request-level control.
/// </remarks>
public enum PromptCacheReuseHint {
    /// <summary>Use the connection/client's configured cache behavior.</summary>
    ConnectionDefault,

    /// <summary>The request prefix is not expected to be used by a later request.</summary>
    NoReuseExpected,

    /// <summary>The prefix is expected to be reused during a nearby active loop.</summary>
    ReuseExpectedSoon,

    /// <summary>The prefix is expected to be reused after a human-paced pause.</summary>
    ReuseExpectedAfterPause,
}

/// <summary>
/// Operational options for one completion invocation. These options are kept
/// separate from <see cref="CompletionRequest"/> because they do not change the
/// logical model request or its replay identity.
/// </summary>
public sealed record CompletionInvocationOptions {
    public static CompletionInvocationOptions Default { get; } = new();

    public PromptCacheReuseHint PromptCacheReuseHint { get; init; }
        = PromptCacheReuseHint.ConnectionDefault;

    /// <summary>
    /// Best-effort request for readable reasoning text or a provider-generated
    /// summary. Enabled by default and independent of reasoning effort.
    /// </summary>
    /// <remarks>
    /// Does not enable thinking on a model that would otherwise not think and
    /// does not guarantee a non-empty response. Readable content is exposed by
    /// <see cref="ActionBlock.ReasoningBlock.PlainText"/> and
    /// <see cref="CompletionStreamObserver.ReceivedReasoningDelta"/> when available.
    /// False omits the extra text request; it does not redact received content,
    /// suppress observer events, or remove authoritative replay payloads.
    /// Unsupported surfaces may treat this preference as a no-op.
    /// </remarks>
    public bool RequestReasoningText { get; init; } = true;

    /// <summary>Rejects enum values unknown to this version of the contract.</summary>
    public void Validate() {
        if (!Enum.IsDefined(PromptCacheReuseHint)) {
            throw new ArgumentOutOfRangeException(
                nameof(PromptCacheReuseHint),
                PromptCacheReuseHint,
                "Unknown prompt cache reuse hint."
            );
        }
    }
}
