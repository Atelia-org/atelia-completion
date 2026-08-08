using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.Tests.Abstractions;

public sealed class CompletionOutputContractFingerprintTests {
    [Fact]
    public void SemanticFingerprint_IsDeterministicAndCoversPolicy() {
        ToolDefinition tool = CreateTool("submit", "value");
        var first = new CompletionOutputContract(
            [tool],
            CompletionToolChoice.Auto
        );
        var equivalent = new CompletionOutputContract(
            [CreateTool("submit", "value")],
            CompletionToolChoice.Auto
        );
        var changedSchema = new CompletionOutputContract(
            [CreateTool("submit", "other")],
            CompletionToolChoice.Auto
        );
        var changedChoice = new CompletionOutputContract(
            [CreateTool("submit", "value")],
            CompletionToolChoice.RequiredNamed("submit")
        );
        var changedParallel = new CompletionOutputContract(
            [CreateTool("submit", "value")],
            CompletionToolChoice.Auto,
            allowParallelToolCalls: true
        );

        Assert.Matches(
            "^sha256:[0-9a-f]{64}$",
            first.SemanticFingerprint
        );
        Assert.Equal(
            first.SemanticFingerprint,
            equivalent.SemanticFingerprint
        );
        Assert.NotEqual(
            first.SemanticFingerprint,
            changedSchema.SemanticFingerprint
        );
        Assert.NotEqual(
            first.SemanticFingerprint,
            changedChoice.SemanticFingerprint
        );
        Assert.NotEqual(
            first.SemanticFingerprint,
            changedParallel.SemanticFingerprint
        );
    }

    [Fact]
    public void SemanticFingerprint_HandlesAllValidFloatingDefaults() {
        foreach (double value in new[] {
            double.NaN,
            double.NegativeInfinity,
            double.PositiveInfinity,
            -0d
        }) {
            var contract = new CompletionOutputContract(
                [
                    new ToolDefinition(
                        "sample",
                        "Sample.",
                        new ToolSchema.Object([
                            new ToolSchema.Property(
                                "value",
                                new ToolSchema.Value(
                                    ToolParamType.Float64,
                                    defaultValue: new ParamDefault(value)
                                ),
                                true
                            )
                        ])
                    )
                ],
                CompletionToolChoice.ProviderDefault
            );

            Assert.StartsWith(
                "sha256:",
                contract.SemanticFingerprint,
                StringComparison.Ordinal
            );
        }
    }

    private static ToolDefinition CreateTool(
        string name,
        string propertyName
    ) => new(
        name,
        "Submit.",
        new ToolSchema.Object([
            new ToolSchema.Property(
                propertyName,
                new ToolSchema.Value(
                    ToolParamType.String,
                    isNullable: true,
                    stringEnumValues: ["a", "b"],
                    minLength: 1,
                    maxLength: 8,
                    pattern: "^[ab]+$"
                ),
                true
            )
        ])
    );
}
