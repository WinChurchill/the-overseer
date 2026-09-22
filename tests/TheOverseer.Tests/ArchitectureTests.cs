using System.Reflection;

namespace TheOverseer.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void CoreDoesNotDependOnApplicationOrPresentationAssemblies()
    {
        var core = Assembly.Load("TheOverseer.Core");
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "TheOverseer.App",
            "TheOverseer.UI",
            "PresentationCore",
            "PresentationFramework",
            "WindowsBase",
            "System.Windows.Forms",
        };

        Assert.DoesNotContain(core.GetReferencedAssemblies(),
            reference => reference.Name is not null && forbidden.Contains(reference.Name));
    }
}
