using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.ModelSpecs.Tests;

public sealed class ReasoningEffortMapperTests {
    private static readonly CompletionReasoningEffort[] Enabled = [
        CompletionReasoningEffort.Low, CompletionReasoningEffort.Medium,
        CompletionReasoningEffort.High, CompletionReasoningEffort.XHigh,
        CompletionReasoningEffort.Max
    ];

    [Fact]
    public void All31NonemptySets_PreserveSupportedValuesAreMonotonicAndIdempotent() {
        for (int mask = 1; mask < 32; mask++) {
            CompletionReasoningEffort[] supported = Enabled
                .Where((_, index) => (mask & (1 << index)) != 0).ToArray();
            ICompletionReasoningEffortMapper mapper = ReasoningEffortMappers.ForSupportedLevels(supported);
            int priorRank = -1;
            foreach (CompletionReasoningEffort requested in Enabled) {
                ReasoningEffortMapping result = mapper.Map(requested);
                Assert.Contains(result.EffectiveEffort, supported);
                Assert.Equal(result, mapper.Map(result.EffectiveEffort));
                int resultRank = Array.IndexOf(Enabled, result.EffectiveEffort);
                Assert.True(resultRank >= priorRank, $"Non-monotonic mask {mask} at {requested}.");
                priorRank = resultRank;
                Assert.False(string.IsNullOrWhiteSpace(result.WireLevel));
                if (supported.Contains(requested)) {
                    Assert.Equal(requested, result.EffectiveEffort);
                }

                if (requested is CompletionReasoningEffort.Medium) {
                    int nearestDistance = supported.Min(level => Math.Abs(Array.IndexOf(Enabled, level) - 1));
                    Assert.Equal(nearestDistance, Math.Abs(resultRank - 1));
                    Assert.DoesNotContain(supported, level =>
                        Math.Abs(Array.IndexOf(Enabled, level) - 1) == nearestDistance
                        && Array.IndexOf(Enabled, level) > resultRank);
                }
            }

            Assert.Equal(supported[0], mapper.Map(CompletionReasoningEffort.Disabled).EffectiveEffort);
            var withDisabled = ReasoningEffortMappers.ForSupportedLevels(supported, supportsDisabled: true);
            Assert.Equal(new(CompletionReasoningEffort.Disabled, null), withDisabled.Map(CompletionReasoningEffort.Disabled));
            foreach (CompletionReasoningEffort effort in Enabled) {
                Assert.Equal(mapper.Map(effort), withDisabled.Map(effort));
            }
        }
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.Disabled, CompletionReasoningEffort.Low)]
    [InlineData(CompletionReasoningEffort.Low, CompletionReasoningEffort.Low)]
    [InlineData(CompletionReasoningEffort.Medium, CompletionReasoningEffort.High)]
    [InlineData(CompletionReasoningEffort.High, CompletionReasoningEffort.High)]
    [InlineData(CompletionReasoningEffort.XHigh, CompletionReasoningEffort.High)]
    [InlineData(CompletionReasoningEffort.Max, CompletionReasoningEffort.Max)]
    public void LowHighMax_FollowsPublishedPolicy(
        CompletionReasoningEffort requested, CompletionReasoningEffort expected
    ) {
        var mapper = ReasoningEffortMappers.ForSupportedLevels([
            CompletionReasoningEffort.Low, CompletionReasoningEffort.High, CompletionReasoningEffort.Max
        ]);
        Assert.Equal(expected, mapper.Map(requested).EffectiveEffort);
    }

    [Fact]
    public void SparseLowMax_XHighRoundsTowardMiddleRatherThanNearest() {
        var mapper = ReasoningEffortMappers.ForSupportedLevels([
            CompletionReasoningEffort.Low, CompletionReasoningEffort.Max
        ]);
        Assert.Equal(new(CompletionReasoningEffort.Low, "low"), mapper.Map(CompletionReasoningEffort.XHigh));
        Assert.Equal(new(CompletionReasoningEffort.Max, "max"), mapper.Map(CompletionReasoningEffort.Max));
    }

    [Fact]
    public void NamedLevels_SemanticsAndNamesRemainIndependentAndInputsFreeze() {
        var names = new Dictionary<CompletionReasoningEffort, string> {
            [CompletionReasoningEffort.Disabled] = "off",
            [CompletionReasoningEffort.Low] = "standard",
            [CompletionReasoningEffort.High] = "extended"
        };
        var mapper = ReasoningEffortMappers.ForNamedLevels(names);
        names[CompletionReasoningEffort.High] = "mutated";
        names.Add(CompletionReasoningEffort.Medium, "mutated-medium");
        Assert.Equal(new(CompletionReasoningEffort.Disabled, "off"), mapper.Map(CompletionReasoningEffort.Disabled));
        Assert.Equal(new(CompletionReasoningEffort.Low, "standard"), mapper.Map(CompletionReasoningEffort.Low));
        foreach (CompletionReasoningEffort effort in Enabled.Skip(1)) {
            Assert.Equal(new(CompletionReasoningEffort.High, "extended"), mapper.Map(effort));
        }
    }

    [Fact]
    public void StandardAndNamedFactories_ShareAllEnabledRoundingResults() {
        for (int mask = 1; mask < 32; mask++) {
            var supported = Enabled.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            var standard = ReasoningEffortMappers.ForSupportedLevels(supported);
            var named = ReasoningEffortMappers.ForNamedLevels(supported.ToDictionary(
                level => level, level => standard.Map(level).WireLevel!));
            foreach (var requested in Enabled.Prepend(CompletionReasoningEffort.Disabled)) {
                Assert.Equal(standard.Map(requested), named.Map(requested));
            }
        }
    }

    [Fact]
    public void SupportedInputFreezes_AndInvalidConfigurationsReject() {
        CompletionReasoningEffort[] levels = [CompletionReasoningEffort.Low, CompletionReasoningEffort.High];
        var mapper = ReasoningEffortMappers.ForSupportedLevels(levels);
        levels[1] = CompletionReasoningEffort.Medium;
        Assert.Equal(CompletionReasoningEffort.High, mapper.Map(CompletionReasoningEffort.Medium).EffectiveEffort);
        Assert.Throws<ArgumentException>(() => ReasoningEffortMappers.ForSupportedLevels([]));
        Assert.Throws<ArgumentException>(() => ReasoningEffortMappers.ForSupportedLevels([
            CompletionReasoningEffort.Low, CompletionReasoningEffort.Low]));
        foreach (var invalid in new[] { CompletionReasoningEffort.ProviderDefault, CompletionReasoningEffort.Disabled, (CompletionReasoningEffort)99 }) {
            Assert.Throws<ArgumentException>(() => ReasoningEffortMappers.ForSupportedLevels([invalid]));
        }

        Assert.Throws<ArgumentException>(() => ReasoningEffortMappers.ForNamedLevels(new Dictionary<CompletionReasoningEffort, string>()));
        Assert.Throws<ArgumentException>(() => ReasoningEffortMappers.ForNamedLevels(new Dictionary<CompletionReasoningEffort, string> {
            [CompletionReasoningEffort.Disabled] = "off"
        }));
        foreach (var invalid in new[] { CompletionReasoningEffort.ProviderDefault, (CompletionReasoningEffort)99 }) {
            Assert.Throws<ArgumentException>(() => ReasoningEffortMappers.ForNamedLevels(new Dictionary<CompletionReasoningEffort, string> {
                [CompletionReasoningEffort.Low] = "low", [invalid] = "name"
            }));
        }

        foreach (string? invalid in new string?[] { null, "", " " }) {
            Assert.Throws<ArgumentException>(() => ReasoningEffortMappers.ForNamedLevels(new Dictionary<CompletionReasoningEffort, string> {
                [CompletionReasoningEffort.Low] = invalid!
            }));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Map(CompletionReasoningEffort.ProviderDefault));
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Map((CompletionReasoningEffort)99));
    }

    [Fact]
    public void Resolver_ProviderDefaultNeverCallsMapper_ExplicitUnknownUsesAdapterNaming() {
        var mapper = new CountingMapper(new(CompletionReasoningEffort.Max, "custom"));
        var spec = new CompletionModelSpec { ReasoningMapper = mapper };
        Assert.Null(ModelSpecReasoning.Resolve(CompletionReasoningEffort.ProviderDefault, spec));
        Assert.Equal(0, mapper.Calls);
        Assert.Equal(new ReasoningEffortMapping(CompletionReasoningEffort.Max, "custom"),
            ModelSpecReasoning.Resolve(CompletionReasoningEffort.Low, spec));
        Assert.Equal(1, mapper.Calls);
        Assert.Equal(new ReasoningEffortMapping(CompletionReasoningEffort.High, null),
            ModelSpecReasoning.Resolve(CompletionReasoningEffort.High, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelSpecReasoning.Resolve((CompletionReasoningEffort)99, null));
    }

    [Theory]
    [InlineData(CompletionReasoningEffort.ProviderDefault, null)]
    [InlineData((CompletionReasoningEffort)99, "custom")]
    [InlineData(CompletionReasoningEffort.Low, "")]
    [InlineData(CompletionReasoningEffort.Disabled, " ")]
    public void Resolver_InvalidExtensionResultsAreConfigurationErrors(CompletionReasoningEffort effective, string? name) {
        var spec = new CompletionModelSpec { ReasoningMapper = new CountingMapper(new(effective, name)) };
        Assert.Throws<ArgumentException>(() => ModelSpecReasoning.Resolve(CompletionReasoningEffort.Low, spec));
    }

    private sealed class CountingMapper(ReasoningEffortMapping result) : ICompletionReasoningEffortMapper {
        internal int Calls { get; private set; }
        public ReasoningEffortMapping Map(CompletionReasoningEffort requested) {
            Calls++;
            return result;
        }
    }
}
