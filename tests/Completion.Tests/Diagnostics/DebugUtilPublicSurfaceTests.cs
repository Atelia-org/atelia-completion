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
}
