using System.Reflection;
using System.Runtime.CompilerServices;
using Atelia.Completion.OpenAI;
using Xunit;

namespace Atelia.Completion.Tests;

public sealed class AssemblyBoundaryTests {
    [Fact]
    public void ImplementationOnlyGrantsFriendAccessToItsOwnTests() {
        var friends = typeof(OpenAIChatClient).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Atelia.Completion.Tests" }, friends);
    }
}
