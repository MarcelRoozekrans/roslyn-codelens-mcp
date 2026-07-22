using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// End-to-end over an MSBuild-loaded solution rather than a hand-built AdhocWorkspace. The logic
/// tests pin the filtering rules; these pin that the tool survives a real multi-project solution —
/// several compilations, real metadata references, and a project graph the unit fixture does not
/// reproduce.
/// </summary>
[Collection("TestSolution")]
public class GetInstantiationOptionsFixtureTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetInstantiationOptionsFixtureTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    private InstantiationOptionsResult Run(string symbol, string? fromProject = null)
        => GetInstantiationOptionsLogic.Execute(_loaded, _resolver, symbol, fromProject);

    [Fact]
    public void Reports_a_real_constructor_with_its_parameters()
    {
        var result = Run("OrderService");

        Assert.True(result.Instantiable);
        var ctor = Assert.Single(result.Constructors);
        var parameter = Assert.Single(ctor.Parameters);
        Assert.Equal("repo", parameter.Name);
        Assert.Contains("IOrderRepo", parameter.Type, StringComparison.Ordinal);
        Assert.Equal("public", ctor.Accessibility);
        Assert.NotNull(ctor.File);
    }

    [Fact]
    public void Interface_from_the_real_solution_is_not_instantiable()
    {
        var result = Run("IOrderRepo");

        Assert.False(result.Instantiable);
        Assert.Empty(result.Constructors);
        Assert.Contains("find_implementations", result.Note!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The accessibility path resolves the caller project, re-resolves the type inside that
    /// project's own compilation, and asks Roslyn — none of which the AdhocWorkspace tests exercise
    /// against a real project graph.
    /// </summary>
    [Fact]
    public void Accessibility_is_computed_against_a_real_project()
    {
        var result = Run("OrderService", "TestLib");

        Assert.All(result.Constructors, c => Assert.True(c.Accessible));
    }

    [Fact]
    public void Accessible_stays_null_when_no_caller_project_is_named()
    {
        Assert.All(Run("OrderService").Constructors, c => Assert.Null(c.Accessible));
    }
}
