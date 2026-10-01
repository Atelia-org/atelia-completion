using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.Completion.ModelSpecs.Tests;

public sealed class CompletionModelSpecCatalogTests {
    [Fact]
    public void ExactLongestPrefixGlobalAndMiss_RespectOrdinalAndZeroLengthSuffix() {
        var exact = new CompletionModelSpec();
        var family = new CompletionModelSpec { OutputTokenLimit = 10 };
        var narrower = new CompletionModelSpec { OutputTokenLimit = 20 };
        var global = new CompletionModelSpec { OutputTokenLimit = 30 };
        var catalog = CompletionModelSpecCatalog.Empty
            .WithPattern("*", global).WithPattern("claude-*", family)
            .WithPattern("claude-opus-*", narrower).WithModel("claude-opus-known", exact);
        Assert.Same(exact, catalog.Lookup("claude-opus-known"));
        Assert.Same(narrower, catalog.Lookup("claude-opus-future"));
        Assert.Same(narrower, catalog.Lookup("claude-opus-"));
        Assert.Same(family, catalog.Lookup("claude-other"));
        Assert.Same(family, catalog.Lookup("claude-"));
        Assert.Same(global, catalog.Lookup("Claude-opus-known"));
        Assert.Same(global, catalog.Lookup("other"));
        Assert.Null(CompletionModelSpecCatalog.Empty.Lookup("claude-opus-known"));
        Assert.Null(exact.OutputTokenLimit); // Exact rules do not borrow missing fields.
        Assert.Null(exact.ReasoningMapper);
    }

    [Fact]
    public void RegistrationOrderDoesNotAffectSelectionAndFieldsNeverMerge() {
        var empty = new CompletionModelSpec();
        var cap = new CompletionModelSpec { OutputTokenLimit = 42 };
        (string Pattern, CompletionModelSpec Spec)[] patterns = [
            ("*", cap), ("model-*", cap), ("model-long-*", empty)
        ];
        foreach (var permutation in Permutations(patterns)) {
            var catalog = new CompletionModelSpecCatalog(patterns: permutation)
                .WithModel("model-known", empty);
            Assert.Same(empty, catalog.Lookup("model-known"));
            Assert.Same(empty, catalog.Lookup("model-long-new"));
            Assert.Null(catalog.Lookup("model-long-new")!.OutputTokenLimit);
            Assert.Same(cap, catalog.Lookup("model-new"));
            Assert.Same(cap, catalog.Lookup("other"));
        }
    }

    [Fact]
    public void AliasesFreezeAndSingleOverrideDoesNotMutateCompanionsOrOldCatalog() {
        var spec = new CompletionModelSpec { OutputTokenLimit = 42 };
        var changed = new CompletionModelSpec();
        List<string> ids = ["route-a", "route-b"];
        var catalog = CompletionModelSpecCatalog.Empty.WithModels(ids, spec);
        ids[0] = "changed-input";
        ids.Add("third-input");
        var updated = catalog.WithModel("route-a", changed);
        Assert.Same(spec, catalog.Lookup("route-a"));
        Assert.Same(changed, updated.Lookup("route-a"));
        Assert.Same(spec, updated.Lookup("route-b"));
        Assert.Null(catalog.Lookup("changed-input"));
        Assert.Null(catalog.Lookup("third-input"));
        var sequential = CompletionModelSpecCatalog.Empty.WithModel("route-a", spec).WithModel("route-b", spec);
        foreach (string id in new[] { "route-a", "route-b", "missing" }) {
            Assert.Same(catalog.Lookup(id), sequential.Lookup(id));
        }

        var independent = CompletionModelSpecCatalog.Empty.WithModel("route-a", changed);
        Assert.Same(changed, independent.Lookup("route-a"));
        Assert.Same(spec, catalog.Lookup("route-a"));
    }

    [Fact]
    public void BatchCopiesInputAndRejectsDuplicatesWithinOrAcrossLists() {
        var spec = new CompletionModelSpec();
        string[] ids = ["a", "b"];
        var rules = new List<(IReadOnlyList<string>, CompletionModelSpec)> { (ids, spec) };
        var catalog = new CompletionModelSpecCatalog(rules);
        ids[0] = "mutated";
        rules.Clear();
        Assert.Same(spec, catalog.Lookup("a"));
        Assert.Null(catalog.Lookup("mutated"));
        Assert.Throws<ArgumentException>(() => new CompletionModelSpecCatalog(models: [(["a", "a"], spec)]));
        Assert.Throws<ArgumentException>(() => new CompletionModelSpecCatalog(models: [(["a", "b"], spec), (["b"], spec)]));
        Assert.Throws<ArgumentException>(() => new CompletionModelSpecCatalog(patterns: [("route-*", spec), ("route-*", spec)]));
        Assert.Throws<ArgumentNullException>(() => new CompletionModelSpecCatalog(models: [(["a"], null!)]));
        Assert.Throws<ArgumentNullException>(() => new CompletionModelSpecCatalog(patterns: [("*", null!)]));
    }

    [Fact]
    public void ExplicitUpdatesReplaceWholeRule_AndFailureCannotPartiallyUpdateOriginal() {
        var original = new CompletionModelSpec { OutputTokenLimit = 42 };
        var replacement = new CompletionModelSpec();
        var catalog = CompletionModelSpecCatalog.Empty.WithModel("a", original).WithPattern("route-*", original);
        var updated = catalog.WithModels(["a", "b"], replacement).WithPattern("route-*", replacement);
        Assert.Same(replacement, updated.Lookup("a"));
        Assert.Same(replacement, updated.Lookup("b"));
        Assert.Same(replacement, updated.Lookup("route-new"));
        Assert.Same(original, catalog.Lookup("a"));
        Assert.Same(original, catalog.Lookup("route-new"));
        Assert.Throws<ArgumentException>(() => catalog.WithModels(["a", "invalid*"], replacement));
        Assert.Same(original, catalog.Lookup("a"));
        Assert.Null(catalog.Lookup("b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("*")]
    [InlineData("route-*")]
    [InlineData("route?")]
    [InlineData("route[ab]")]
    public void InvalidExactIdsReject(string? invalid) {
        Assert.ThrowsAny<ArgumentException>(() => CompletionModelSpecCatalog.Empty.WithModel(invalid!, new()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("no-star")]
    [InlineData("mid*value")]
    [InlineData("many**")]
    [InlineData("route?*")]
    [InlineData("route[ab]*")]
    public void InvalidPatternsReject(string? invalid) {
        Assert.ThrowsAny<ArgumentException>(() => CompletionModelSpecCatalog.Empty.WithPattern(invalid!, new()));
    }

    [Fact]
    public void ListsAreNonemptyAndOrdinalAndSpecsRequirePositiveLimits() {
        var spec = new CompletionModelSpec();
        Assert.Throws<ArgumentException>(() => CompletionModelSpecCatalog.Empty.WithModels([], spec));
        Assert.Throws<ArgumentNullException>(() => CompletionModelSpecCatalog.Empty.WithModels(null!, spec));
        Assert.Throws<ArgumentException>(() => CompletionModelSpecCatalog.Empty.WithModels(["a", "a"], spec));
        var catalog = CompletionModelSpecCatalog.Empty.WithModels(["Route", "route"], spec);
        Assert.Same(spec, catalog.Lookup("Route"));
        Assert.Same(spec, catalog.Lookup("route"));
        Assert.Null(catalog.Lookup("ROUTE"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompletionModelSpec { OutputTokenLimit = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompletionModelSpec { OutputTokenLimit = -1 });
        Assert.Null(new CompletionModelSpec().OutputTokenLimit);
        Assert.Equal(int.MaxValue, new CompletionModelSpec { OutputTokenLimit = int.MaxValue }.OutputTokenLimit);
    }

    [Fact]
    public void BuiltinsAreExactAndScopedAndNoFilenameAliasIsInvented() {
        Assert.NotNull(BuiltinModelSpecs.StandardChat.Lookup("glm-5.3")?.ReasoningMapper);
        Assert.Null(BuiltinModelSpecs.DeepSeekChat.Lookup("glm-5.3"));
        Assert.NotNull(BuiltinModelSpecs.DeepSeekChat.Lookup("deepseek-flash")?.ReasoningMapper);
        Assert.Null(BuiltinModelSpecs.DeepSeekChat.Lookup("deepseek-v4-1-flash"));
        foreach (string id in new[] { "claude-opus-4-6", "claude-opus-4-7", "claude-opus-4-8", "claude-opus-5", "claude-opus-5-5" }) {
            Assert.Equal(128_000, BuiltinModelSpecs.AnthropicMessages.Lookup(id)!.OutputTokenLimit);
        }

        Assert.Null(BuiltinModelSpecs.AnthropicMessages.Lookup("claude-opus-future"));
        var opus = BuiltinModelSpecs.AnthropicMessages.Lookup("claude-opus-5-5")!;
        Assert.False(opus.ForcedToolChoiceSupported);
        Assert.Equal(CompletionReasoningEffort.Low, opus.ReasoningMapper!.Map(CompletionReasoningEffort.Disabled).EffectiveEffort);
        Assert.Equal(CompletionReasoningEffort.XHigh, opus.ReasoningMapper.Map(CompletionReasoningEffort.XHigh).EffectiveEffort);
        var withFallback = BuiltinModelSpecs.AnthropicMessages.WithPattern("claude-*", new() { OutputTokenLimit = 10 });
        Assert.Same(opus, withFallback.Lookup("claude-opus-5-5"));
        Assert.Null(BuiltinModelSpecs.AnthropicMessages.Lookup("claude-new"));
        Assert.Equal(10, withFallback.Lookup("claude-new")!.OutputTokenLimit);
    }

    private static IEnumerable<T[]> Permutations<T>(T[] items) {
        if (items.Length == 0) {
            yield return [];
            yield break;
        }

        for (int i = 0; i < items.Length; i++) {
            foreach (var rest in Permutations(items.Where((_, index) => index != i).ToArray())) {
                yield return [items[i], .. rest];
            }
        }
    }
}
