using RoslynCodeGraph;

namespace RoslynCodeGraph.Tests;

public class SymbolResolverTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindBySimpleName_ReturnsMatches()
    {
        var resolver = new SymbolResolver(_loaded);
        var results = resolver.FindNamedTypes("Greeter");

        Assert.Contains(results, s => s.Name == "Greeter");
    }

    [Fact]
    public void FindByFullName_ReturnsExactMatch()
    {
        var resolver = new SymbolResolver(_loaded);
        var results = resolver.FindNamedTypes("TestLib.Greeter");

        Assert.Single(results);
        Assert.Equal("TestLib.Greeter", results[0].ToDisplayString());
    }

    [Fact]
    public void FindMethods_ReturnsBySymbolName()
    {
        var resolver = new SymbolResolver(_loaded);
        var results = resolver.FindMethods("Greeter.Greet");

        Assert.NotEmpty(results);
    }

    [Fact]
    public void IsGenerated_ReturnsFalse_ForRegularFiles()
    {
        var resolver = new SymbolResolver(_loaded);
        var types = resolver.FindNamedTypes("Greeter");
        Assert.NotEmpty(types);
        var (file, _) = resolver.GetFileAndLine(types[0]);
        Assert.NotEmpty(file);
        Assert.False(resolver.IsGenerated(file));
    }

    [Fact]
    public void IsGenerated_ReturnsTrue_ForObjPaths()
    {
        var resolver = new SymbolResolver(_loaded);
        Assert.True(resolver.IsGenerated(@"C:\project\obj\Debug\net10.0\Generated.cs"));
        Assert.True(resolver.IsGenerated(@"C:\project\obj\Release\net10.0\SomeGen.g.cs"));
    }

    [Fact]
    public void IsGenerated_ReturnsTrue_ForNullOrEmptyPaths()
    {
        var resolver = new SymbolResolver(_loaded);
        Assert.True(resolver.IsGenerated(""));
        Assert.True(resolver.IsGenerated(null!));
    }
}
