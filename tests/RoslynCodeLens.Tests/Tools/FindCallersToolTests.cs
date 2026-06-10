using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindCallersToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public FindCallersToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindCallers_ForMethod_ReturnsCallSites()
    {
        var results = FindCallersLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.Contains(results, r => r.Caller.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindCallers_MetadataExtensionMethod_FindsSourceInvocations()
    {
        var results = FindCallersLogic.Execute(
            _loaded, _resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped");

        Assert.NotEmpty(results);
    }

    [Fact]
    public void FindCallers_InterfaceMethod_FindsCallersInReferencingProject()
    {
        // IGreeter.Greet is defined in TestLib; GreeterConsumer in TestLib2 calls it via the interface.
        // Without cross-compilation symbol normalisation, the interface dispatch check uses
        // reference-equality symbols, missing callers from other projects.
        var results = FindCallersLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.Contains(results, r => r.File.Contains("TestLib2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sort_OrdersByFileThenLine()
    {
        var input = new List<CallerInfo>
        {
            new("Caller", "b.cs", 1, "x", "P"),
            new("Caller", "a.cs", 9, "x", "P"),
            new("Caller", "a.cs", 2, "x", "P"),
        };

        var sorted = FindCallersTool.Sort(input);

        Assert.Collection(sorted,
            c => { Assert.Equal("a.cs", c.File); Assert.Equal(2, c.Line); },
            c => { Assert.Equal("a.cs", c.File); Assert.Equal(9, c.Line); },
            c => { Assert.Equal("b.cs", c.File); Assert.Equal(1, c.Line); });
    }

    [Fact]
    public void BuildSummary_GroupsByProject()
    {
        var input = new List<CallerInfo>
        {
            new("Caller", "a.cs", 1, "x", "Foo"),
            new("Caller", "a.cs", 2, "x", "Foo"),
            new("Caller", "b.cs", 1, "x", "Bar"),
        };

        var summary = FindCallersTool.BuildSummary(input);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"Foo\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"Bar\":1", json, StringComparison.Ordinal);
    }
}
