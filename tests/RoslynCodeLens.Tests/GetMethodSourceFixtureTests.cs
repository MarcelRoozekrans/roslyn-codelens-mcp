using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

[Collection("TestSolution")]
public class GetMethodSourceFixtureTests
{
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public GetMethodSourceFixtureTests(TestSolutionFixture fixture)
    {
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void GreeterGreet_ReturnsRealBody()
    {
        var item = Assert.Single(
            GetMethodSourceLogic.Execute(_resolver, _metadata, ["Greeter.Greet"]));

        Assert.Equal("ok", item.Status);
        Assert.Equal("method", item.Kind);
        // Distinctive body line from Fixtures/TestSolution/TestLib/Greeter.cs.
        Assert.Contains(
            "public virtual string Greet(string name) => $\"Hello, {name}!\";",
            item.Source, StringComparison.Ordinal);
        Assert.Equal("TestLib", item.Project);
        Assert.EndsWith("Greeter.cs", item.File, StringComparison.Ordinal);
    }
}
