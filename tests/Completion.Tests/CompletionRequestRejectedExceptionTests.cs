using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class CompletionRequestRejectedExceptionTests {
    [Fact]
    public void Constructor_FreezesCallerDiagnosticsAndRetainsNoInnerException() {
        var errors = new List<string> {
            "http-status=403",
            "request-id=req_safe-123"
        };
        var termination = CompletionTermination.Failed(
            "provider.request-rejected",
            "The provider rejected the request before streaming."
        );

        var exception = new CompletionRequestRejectedException(
            termination,
            errors
        );
        errors[0] = "mutated";
        errors.Add("late");

        Assert.Same(termination, exception.Termination);
        Assert.Equal(
            ["http-status=403", "request-id=req_safe-123"],
            exception.Errors
        );
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Constructor_RequiresFailedTerminationAndStableProviderReason() {
        Assert.Throws<ArgumentException>(() =>
            new CompletionRequestRejectedException(
                CompletionTermination.Completed("provider.completed")
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new CompletionRequestRejectedException(
                CompletionTermination.Failed(" ")
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new CompletionRequestRejectedException(
                CompletionTermination.Failed("provider reason")
            )
        );
    }

    [Fact]
    public void Constructor_RejectsUnboundedOrControlBearingDiagnostics() {
        Assert.Throws<ArgumentException>(() =>
            new CompletionRequestRejectedException(
                CompletionTermination.Failed(
                    "provider.rejected",
                    "unsafe\nraw body"
                )
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new CompletionRequestRejectedException(
                CompletionTermination.Failed("provider.rejected"),
                [new string('x', CompletionRequestRejectedException.MaximumErrorCharacters + 1)]
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new CompletionRequestRejectedException(
                CompletionTermination.Failed("provider.rejected"),
                Enumerable.Repeat(
                    "bounded",
                    CompletionRequestRejectedException.MaximumErrorCount + 1
                ).ToArray()
            )
        );
    }
}
