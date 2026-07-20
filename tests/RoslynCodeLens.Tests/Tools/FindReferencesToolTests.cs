using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindReferencesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public FindReferencesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindReferences_ForInterface_ReturnsUsages()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_ForMethod_ReturnsCallSites()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_UnknownSymbol_ReturnsEmpty()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "NonExistent");

        Assert.Empty(results);
    }

    [Fact]
    public void FindReferences_MetadataInterface_FindsSourceUsages()
    {
        var results = FindReferencesLogic.Execute(
            _loaded, _resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(!string.IsNullOrEmpty(r.File)));
    }

    [Fact]
    public void FindReferences_ForInterface_FindsUsagesInReferencingProject()
    {
        // IGreeter is defined in TestLib; GreeterConsumer and CrossProjectGreeter in TestLib2 reference it.
        // Without cross-compilation symbol normalisation, cross-project usages are missed
        // because the target set is built with reference-equality symbols from one compilation.
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");

        Assert.Contains(results, r => r.File.Contains("TestLib2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sort_OrdersByFileThenLine()
    {
        var input = new List<SymbolReference>
        {
            new("read", "b.cs", 1, 1, "x", "P"),
            new("read", "a.cs", 9, 1, "x", "P"),
            new("read", "a.cs", 2, 1, "x", "P"),
        };

        var sorted = FindReferencesTool.Sort(input);

        Assert.Collection(sorted,
            r => { Assert.Equal("a.cs", r.File); Assert.Equal(2, r.Line); },
            r => { Assert.Equal("a.cs", r.File); Assert.Equal(9, r.Line); },
            r => { Assert.Equal("b.cs", r.File); Assert.Equal(1, r.Line); });
    }

    private static List<SymbolReference> SampleRefs() =>
    [
        new("write", "a.cs", 1, 5, "x", "P"),
        new("read", "a.cs", 1, 10, "x", "P"),
        new("readwrite", "a.cs", 2, 5, "x", "P"),
        new("invocation", "b.cs", 3, 5, "x", "P"),
    ];

    [Fact]
    public void KindsFilter_NarrowsResults()
    {
        var result = FindReferencesTool.BuildResult(SampleRefs(), ["write", "readwrite"], limit: 500);

        Assert.Equal(["write", "readwrite"], result.Items.Select(r => r.ReferenceKind));
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public void KindsFilter_AppliesBeforeLimit()
    {
        var result = FindReferencesTool.BuildResult(SampleRefs(), ["write", "readwrite"], limit: 1);

        Assert.Single(result.Items);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public void NoKindsFilter_ReturnsEverything()
    {
        var result = FindReferencesTool.BuildResult(SampleRefs(), kinds: null, limit: 500);

        Assert.Equal(4, result.TotalCount);
    }

    [Fact]
    public void UnknownKind_Throws()
    {
        var ex = Assert.Throws<McpToolException>(() => FindReferencesTool.ValidateKinds(["write", "bogus"]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("bogus", ex.Message, StringComparison.Ordinal);
        var details = System.Text.Json.JsonSerializer.Serialize(ex.Details);
        Assert.Contains("readwrite", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidKinds_DoNotThrow()
        => FindReferencesTool.ValidateKinds([.. RoslynCodeLens.Analysis.ReferenceClassifier.AllKinds]);

    [Fact]
    public void ByKind_SummaryMatchesItems()
    {
        var result = FindReferencesTool.BuildResult(SampleRefs(), kinds: null, limit: 500);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Summary);

        Assert.Contains("byKind", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"write\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"read\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"readwrite\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"invocation\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Sort_OrdersByFileThenLineThenColumn()
    {
        var input = new List<SymbolReference>
        {
            new("read", "a.cs", 1, 10, "x", "P"),
            new("write", "a.cs", 1, 5, "x", "P"),
        };

        var sorted = FindReferencesTool.Sort(input);

        Assert.Equal([5, 10], sorted.Select(r => r.Column));
    }

    [Fact]
    public void BuildSummary_GroupsByProject()
    {
        var input = new List<SymbolReference>
        {
            new("read", "a.cs", 1, 1, "x", "Foo"),
            new("read", "a.cs", 2, 1, "x", "Foo"),
            new("read", "b.cs", 1, 1, "x", "Bar"),
        };

        var summary = FindReferencesTool.BuildSummary(input);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"Foo\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"Bar\":1", json, StringComparison.Ordinal);
    }
}
