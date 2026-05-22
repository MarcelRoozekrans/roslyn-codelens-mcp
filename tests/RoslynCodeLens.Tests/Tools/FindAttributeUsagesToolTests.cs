using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindAttributeUsagesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public FindAttributeUsagesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindAttributeUsages_Obsolete_FindsMarkedMember()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "Obsolete");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("OldGreet", StringComparison.Ordinal) && string.Equals(r.TargetKind, "method", StringComparison.Ordinal));
    }

    [Fact]
    public void FindAttributeUsages_Serializable_FindsMarkedType()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "Serializable");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("Greeter", StringComparison.Ordinal) && string.Equals(r.TargetKind, "class", StringComparison.Ordinal));
    }

    [Fact]
    public void FindAttributeUsages_WithSuffix_StillMatches()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "ObsoleteAttribute");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("OldGreet", StringComparison.Ordinal));
    }

    [Fact]
    public void FindAttributeUsages_NoMatch_ReturnsEmpty()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "NonExistentAttribute");

        Assert.Empty(results);
    }

    [Fact]
    public void FindAttributeUsages_FullyQualifiedMetadata_FindsUsage()
    {
        // When the caller passes the fully qualified name of a metadata attribute
        // (which is not in the simple-name index), the metadata resolver should
        // locate the attribute type and a full scan should still surface usages.
        var results = FindAttributeUsagesLogic.Execute(
            _loaded, _resolver, _metadata, "System.ObsoleteAttribute");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.TargetName.Contains("OldGreet", StringComparison.Ordinal));
    }

    [Fact]
    public void Sort_OrdersByFileThenLine()
    {
        var input = new List<AttributeUsageInfo>
        {
            new("Obsolete", "method", "X", "b.cs", 1, "P"),
            new("Obsolete", "method", "X", "a.cs", 9, "P"),
            new("Obsolete", "method", "X", "a.cs", 2, "P"),
        };

        var sorted = FindAttributeUsagesTool.Sort(input);

        Assert.Collection(sorted,
            a => { Assert.Equal("a.cs", a.File); Assert.Equal(2, a.Line); },
            a => { Assert.Equal("a.cs", a.File); Assert.Equal(9, a.Line); },
            a => { Assert.Equal("b.cs", a.File); Assert.Equal(1, a.Line); });
    }

    [Fact]
    public void BuildSummary_GroupsByProject()
    {
        var input = new List<AttributeUsageInfo>
        {
            new("Obsolete", "method", "X", "a.cs", 1, "Foo"),
            new("Obsolete", "method", "Y", "a.cs", 2, "Foo"),
            new("Obsolete", "method", "Z", "b.cs", 1, "Bar"),
        };

        var summary = FindAttributeUsagesTool.BuildSummary(input);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"Foo\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"Bar\":1", json, StringComparison.Ordinal);
    }
}
