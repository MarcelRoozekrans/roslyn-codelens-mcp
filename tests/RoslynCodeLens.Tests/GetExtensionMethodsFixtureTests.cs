using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

[Collection("TestSolution")]
public class GetExtensionMethodsFixtureTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public GetExtensionMethodsFixtureTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    private IReadOnlyList<ExtensionMemberInfo> Run(string type, string? nameFilter = null)
        => GetExtensionMethodsLogic.Execute(_loaded, _resolver, _metadata, type, nameFilter);

    [Fact]
    public void BclLinq_IsReportedForARealSolutionCompilation()
    {
        // string is IEnumerable<char>, so Enumerable's extensions reduce against it. This is the
        // end-to-end proof that metadata scanning works over an MSBuild-loaded solution, not just
        // a hand-built AdhocWorkspace.
        var results = Run("string", "chunk");

        var chunk = Assert.Single(results, r => r.Name == "Chunk");
        Assert.Equal("metadata", chunk.Origin);
        Assert.Equal("System.Linq", chunk.Namespace);
        Assert.Equal("System.Linq.Enumerable", chunk.DeclaringType);
        Assert.Null(chunk.File);
    }

    [Fact]
    public void SolutionType_ResolvesAndReportsOnlyApplicableExtensions()
    {
        // TestLib.Greeter is not enumerable, so no LINQ may be reported for it — the negative
        // half of applicability, over the real solution.
        var results = Run("Greeter");

        Assert.DoesNotContain(results, r => r.Namespace == "System.Linq");
        Assert.All(results, r => Assert.Contains(r.Kind, ["method", "property"], StringComparer.Ordinal));
    }

    [Fact]
    public void Tool_ReturnsEnvelopeWithOriginSummary()
    {
        var items = Run("string");
        var envelope = ToolListResult.Create(
            items, limit: 5, GetExtensionMethodsTool.BuildSummary(items));

        Assert.Equal(5, envelope.Items.Count);
        Assert.True(envelope.Truncated);
        Assert.True(envelope.TotalCount > 5);
        Assert.NotNull(envelope.Summary);
    }
}
