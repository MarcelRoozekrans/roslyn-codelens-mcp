using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests;

public class RenameTestWorkspaceTests
{
    [Fact]
    public void Create_ResolvesTypeFromSource()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Widget.cs", "namespace Demo; public class Widget { }"));

        Assert.False(loaded.IsEmpty);
        var symbols = resolver.FindSymbols("Widget");
        Assert.Single(symbols);
        Assert.Equal("Demo.Widget", symbols[0].ToDisplayString());
    }
}
