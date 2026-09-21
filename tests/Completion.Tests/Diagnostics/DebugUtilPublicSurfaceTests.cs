using System.Reflection;
using System.Diagnostics;
using Atelia.Diagnostics;
using Xunit;

namespace Atelia.Completion.Tests.Diagnostics;

public sealed class DebugUtilPublicSurfaceTests {
    [Fact]
    public void PublicSurfaceIsThreeStringOnlyMethods() {
        MethodInfo[] methods = typeof(DebugUtil)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.Equal(
            new[] { "Debug", "Error", "Warning" },
            methods.Select(static method => method.Name).Order(StringComparer.Ordinal)
        );
        Assert.All(
            methods,
            method => Assert.Equal(
                new[] { typeof(string), typeof(string) },
                method.GetParameters().Select(static parameter => parameter.ParameterType)
            )
        );
        Assert.Empty(typeof(DebugUtil).GetNestedTypes(BindingFlags.Public));
    }

    [Fact]
    public void DebugIsCompiledOnlyInDebugBuilds() {
        MethodInfo? method = typeof(DebugUtil).GetMethod(
            nameof(DebugUtil.Debug),
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly
        );

        Assert.NotNull(method);
        ConditionalAttribute? conditional = method.GetCustomAttribute<ConditionalAttribute>();
        Assert.NotNull(conditional);
        Assert.Contains(
            "DEBUG",
            conditional.ConditionString.Split(','),
            StringComparer.Ordinal
        );
    }

    [Fact]
    public void DefaultFileLevelIsDebugAndConsoleLevelIsWarning() {
        Type levelType = Assert.Single(
            typeof(DebugUtil).GetNestedTypes(BindingFlags.NonPublic),
            static type => type.IsEnum
        );
        // GetRawConstantValue() returns the underlying integral value for enum consts,
        // so compare via the numeric contract Debug=0 / Warning=1.
        object? fileLevel = typeof(DebugUtil)
            .GetField("DefaultFileLevel", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetRawConstantValue();
        object? consoleLevel = typeof(DebugUtil)
            .GetField("DefaultConsoleLevel", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetRawConstantValue();

        Assert.Equal(0, fileLevel);
        Assert.Equal(1, consoleLevel);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("debug", 0)]
    [InlineData("DEBUG", 0)]
    [InlineData("warning", 1)]
    [InlineData("error", 2)]
    [InlineData("off", 3)]
    [InlineData("nonsense", 0)]
    public void ParseLevelOrDefaultAppliesFallbackSemantics(string? raw, int expectedLevelValue) {
        MethodInfo? method = typeof(DebugUtil).GetMethod(
            "ParseLevelOrDefault",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);

        Type levelType = Assert.Single(
            typeof(DebugUtil).GetNestedTypes(BindingFlags.NonPublic),
            static type => type.IsEnum
        );
        object fallback = Enum.ToObject(levelType, 0);
        object result = method!.Invoke(null, new object?[] { raw, fallback })!;

        Assert.Equal(expectedLevelValue, Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture));
    }
}
