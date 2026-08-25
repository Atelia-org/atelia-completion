namespace Atelia.Completion.Abstractions;

/// <summary>
/// Reports an authoritative rejection that cannot still produce a completion
/// outcome.
/// </summary>
/// <remarks>
/// A provider adapter may throw this exception only after proving either that
/// local deterministic validation rejected the request before credential or
/// network dispatch, or that the remote endpoint authoritatively rejected the
/// request before any observer delta was emitted. In both cases the request
/// cannot still produce an <see cref="ActionMessage"/>. Transport failures,
/// cancellation, redirects, server failures, malformed protocol data, or
/// interrupted streams do not satisfy that proof.
/// <para>
/// The caller owns content safety: <paramref name="termination"/> detail and
/// <paramref name="errors"/> must be bounded, content-free diagnostics, never
/// raw provider messages, response bodies, credentials, account identifiers,
/// prompts, or generated content. Printable-ASCII validation only constrains
/// shape; it is not a taint sanitizer, so adapters must use code-owned values
/// rather than copying even ASCII-only provider metadata. This type deliberately
/// has no inner-exception constructor so an unsafe provider exception cannot be
/// retained accidentally.
/// </para>
/// </remarks>
public sealed class CompletionRequestRejectedException : Exception {
    public const int MaximumProviderReasonCharacters = 128;
    public const int MaximumDetailCharacters = 1024;
    public const int MaximumErrorCount = 8;
    public const int MaximumErrorCharacters = 256;

    private readonly IReadOnlyList<string> _errors;

    public CompletionRequestRejectedException(
        CompletionTermination termination,
        IReadOnlyList<string>? errors = null
    ) : base("The completion request was authoritatively rejected without a possible completion outcome.") {
        ArgumentNullException.ThrowIfNull(termination);
        if (termination.Kind is not CompletionTerminationKind.Failed) {
            throw new ArgumentException(
                "A request rejection must use CompletionTerminationKind.Failed.",
                nameof(termination)
            );
        }
        if (!IsStableProviderReason(termination.ProviderReason)) {
            throw new ArgumentException(
                "ProviderReason must be a non-blank stable ASCII token of at most 128 characters.",
                nameof(termination)
            );
        }
        ValidateSafeText(
            termination.Detail,
            MaximumDetailCharacters,
            allowNull: true,
            nameof(termination)
        );

        string[] frozenErrors = errors?.ToArray() ?? [];
        if (frozenErrors.Length > MaximumErrorCount) {
            throw new ArgumentException(
                $"Errors must contain at most {MaximumErrorCount} entries.",
                nameof(errors)
            );
        }
        for (int index = 0; index < frozenErrors.Length; index++) {
            ValidateSafeText(
                frozenErrors[index],
                MaximumErrorCharacters,
                allowNull: false,
                $"{nameof(errors)}[{index}]"
            );
        }

        Termination = termination;
        _errors = Array.AsReadOnly(frozenErrors);
    }

    public CompletionTermination Termination { get; }

    public IReadOnlyList<string> Errors => _errors;

    private static bool IsStableProviderReason(string? value) {
        if (value is not { Length: > 0 }
            || value.Length > MaximumProviderReasonCharacters
            || value[0] is < 'a' or > 'z') {
            return false;
        }
        return value.AsSpan(1).IndexOfAnyExcept(
            "abcdefghijklmnopqrstuvwxyz0123456789._:-"
        ) < 0;
    }

    private static void ValidateSafeText(
        string? value,
        int maximumCharacters,
        bool allowNull,
        string parameterName
    ) {
        if (value is null && allowNull) { return; }
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumCharacters
            || value.Any(static character => character is < ' ' or > '~')) {
            throw new ArgumentException(
                $"Value must be non-blank printable ASCII of at most {maximumCharacters} characters.",
                parameterName
            );
        }
    }
}
