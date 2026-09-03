using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tests.Abstractions;

public sealed class CompletionRequestTests {
    [Fact]
    public void PublicContractHasNoCallerSelectedOutputTokenCeiling() {
        Assert.DoesNotContain(
            typeof(CompletionRequest).GetProperties(),
            static property => property.Name.Contains(
                "MaxToken",
                StringComparison.OrdinalIgnoreCase
            )
        );
        Assert.All(
            typeof(CompletionRequest).GetConstructors(),
            constructor => Assert.DoesNotContain(
                constructor.GetParameters(),
                static parameter => parameter.Name?.Contains(
                    "maxToken",
                    StringComparison.OrdinalIgnoreCase
                ) is true
            )
        );
    }

    [Fact]
    public void Constructor_FreezesPrefixAndTailCollections() {
        var shared = new List<IHistoryMessage> {
            new ObservationMessage("shared")
        };
        var tail = new List<IHistoryMessage> {
            new ObservationMessage("tail")
        };
        CompletionOutputContract outputContract =
            CompletionOutputContract.ProviderDefault([]);
        var prefix = new CompletionPromptPrefix(
            "system",
            outputContract,
            shared
        );
        var request = new CompletionRequest(
            "model",
            prefix,
            tail
        );

        shared.Add(new ObservationMessage("later-shared"));
        tail.Add(new ObservationMessage("later-tail"));

        Assert.Same(prefix, request.PromptPrefix);
        Assert.Same(outputContract, request.PromptPrefix.OutputContract);
        Assert.Single(request.PromptPrefix.SharedContextMessages);
        Assert.Single(request.TailMessages);
    }

    [Fact]
    public void RequiredNamed_RequiresExactToolInOrderedSet() {
        var tool = new ToolDefinition(
            "emit_result",
            "Emit one result.",
            new ToolSchema.Object()
        );

        var contract = new CompletionOutputContract(
            [tool],
            CompletionToolChoice.RequiredNamed("emit_result"),
            allowParallelToolCalls: false
        );

        Assert.Same(tool, Assert.Single(contract.Tools));
        Assert.Equal(
            CompletionToolChoiceKind.RequiredNamed,
            contract.ToolChoice.Kind
        );
        Assert.Equal("emit_result", contract.ToolChoice.RequiredToolName);
        Assert.False(contract.AllowParallelToolCalls);
        Assert.Throws<ArgumentException>(
            () => new CompletionOutputContract(
                [tool],
                CompletionToolChoice.RequiredNamed("missing")
            )
        );
    }

    [Fact]
    public void RequiredAny_RejectsEmptyToolSet() {
        Assert.Throws<ArgumentException>(
            () => new CompletionOutputContract(
                ImmutableArray<ToolDefinition>.Empty,
                CompletionToolChoice.RequiredAny
            )
        );
    }
}
