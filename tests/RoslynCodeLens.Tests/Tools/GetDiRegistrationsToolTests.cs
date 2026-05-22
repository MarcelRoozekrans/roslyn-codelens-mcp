using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetDiRegistrationsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetDiRegistrationsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindDiRegistrations_ForIGreeter_ReturnsRegistration()
    {
        var results = GetDiRegistrationsLogic.Execute(_loaded, _resolver, "IGreeter");

        Assert.Single(results);
        Assert.Equal("Scoped", results[0].Lifetime);
        Assert.Contains("Greeter", results[0].Implementation, StringComparison.Ordinal);
    }

    [Fact]
    public void Sort_OrdersByServiceName()
    {
        var input = new List<DiRegistration>
        {
            new("ZService", "Z", "Scoped",  "a.cs", 1),
            new("AService", "A", "Scoped",  "b.cs", 1),
            new("MService", "M", "Scoped",  "c.cs", 1),
        };

        var sorted = GetDiRegistrationsTool.Sort(input);

        Assert.Collection(sorted,
            d => Assert.Equal("AService", d.Service),
            d => Assert.Equal("MService", d.Service),
            d => Assert.Equal("ZService", d.Service));
    }
}
