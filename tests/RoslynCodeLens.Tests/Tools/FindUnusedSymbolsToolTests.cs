using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindUnusedSymbolsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindUnusedSymbolsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindUnusedSymbols_ReturnsResults()
    {
        var (items, _) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, null, false);
        Assert.NotNull(items);
    }

    [Fact]
    public void FindUnusedSymbols_ProjectFilter_FiltersResults()
    {
        var (items, _) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        Assert.All(items, r => Assert.Contains("TestLib", r.Project, StringComparison.Ordinal));
    }

    [Fact]
    public void Sort_OrdersByProjectThenFile()
    {
        var input = new List<UnusedSymbolInfo>
        {
            new("X", "Class",  "z.cs", 1, "Bar"),
            new("X", "Class",  "b.cs", 1, "Foo"),
            new("X", "Method", "a.cs", 9, "Foo"),
        };

        var sorted = FindUnusedSymbolsTool.Sort(input);

        Assert.Collection(sorted,
            u => Assert.Equal("Bar", u.Project),
            u => { Assert.Equal("Foo", u.Project); Assert.Equal("a.cs", u.File); },
            u => { Assert.Equal("Foo", u.Project); Assert.Equal("b.cs", u.File); });
    }

    [Fact]
    public void FindUnusedSymbols_TypeUsedFromAnotherProject_IsNotReportedAsUnused()
    {
        // ICrossProjectOnly is defined in TestLib but has no implementations or usages
        // within TestLib itself — it is only implemented by CrossProjectGreeter in TestLib2.
        // Without cross-compilation symbol normalisation, that cross-project usage is not
        // matched in the referenced-symbol set, causing ICrossProjectOnly to be incorrectly
        // reported as unused.
        var (items, _) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);

        Assert.DoesNotContain(items, i => i.SymbolName.Contains("ICrossProjectOnly", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildSummary_GroupsByKind()
    {
        var input = new List<UnusedSymbolInfo>
        {
            new("X", "Class",  "a.cs", 1, "P"),
            new("Y", "Class",  "a.cs", 2, "P"),
            new("Z", "Method", "a.cs", 3, "P"),
        };

        var summary = FindUnusedSymbolsTool.BuildSummary(input, EmptyCounts());
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"Class\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"Method\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSummary_IncludesFilteredOutWithAllReasonKeys()
    {
        var items = new List<UnusedSymbolInfo>
        {
            new("X", "Class", "a.cs", 1, "P"),
        };
        var filteredCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["testMethod"] = 5,
            ["testContainer"] = 2,
            ["mcpTool"] = 8,
            ["generated"] = 0,
            ["composition"] = 0,
            ["interop"] = 0,
        };

        var summary = FindUnusedSymbolsTool.BuildSummary(items, filteredCounts);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"Class\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"testMethod\":5", json, StringComparison.Ordinal);
        Assert.Contains("\"testContainer\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"mcpTool\":8", json, StringComparison.Ordinal);
        Assert.Contains("\"generated\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"composition\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"interop\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FilteredOut_IncludesXUnitFixtureTestMethods()
    {
        // XUnitFixture has [Fact]-annotated methods. None should appear in items;
        // they should be counted under filteredOut.testMethod or testContainer.
        var (items, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "XUnitFixture", false);

        var fixtureItems = items.Where(i => i.Project == "XUnitFixture").ToList();
        Assert.True(
            fixtureItems.All(i => !i.SymbolName.Contains("Test", StringComparison.Ordinal)),
            $"XUnitFixture test methods leaked into unused list: {string.Join(",", fixtureItems.Select(i => i.SymbolName))}");

        var totalFiltered = counts["testMethod"] + counts["testContainer"];
        Assert.True(totalFiltered > 0,
            $"Expected test-related filtering, got counts={string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    [Fact]
    public void FilteredOut_FixtureProject_HasGeneratedCount()
    {
        var (_, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        Assert.True(counts["generated"] >= 1, "Expected GeneratedClass / GeneratedMember to be filtered");
    }

    [Fact]
    public void FilteredOut_FixtureProject_HasCompositionCount()
    {
        var (_, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        Assert.True(counts["composition"] >= 1, "Expected ExportedService / ImportHost to be filtered");
    }

    [Fact]
    public void FilteredOut_FixtureProject_HasInteropCount()
    {
        var (_, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        Assert.True(counts["interop"] >= 1, "Expected InteropStruct fields to be filtered");
    }

    [Fact]
    public void GeneratedClass_DoesNotAppearAsUnused()
    {
        var (items, _) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        Assert.DoesNotContain(items,
            i => i.SymbolName.Contains("GeneratedClass", StringComparison.Ordinal));
    }

    [Fact]
    public void McpToolExecuteMethods_NeverFlaggedUnused()
    {
        var (items, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);

        Assert.DoesNotContain(items,
            i => i.SymbolName.Contains("SyntheticMcpTool", StringComparison.Ordinal));
        Assert.DoesNotContain(items,
            i => i.SymbolName.EndsWith(".Execute", StringComparison.Ordinal)
              && i.SymbolName.Contains("Synthetic", StringComparison.Ordinal));

        Assert.True(counts["mcpTool"] >= 1, "Expected SyntheticMcpTool to be filtered");
    }

    private static IReadOnlyDictionary<string, int> EmptyCounts()
        => new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["testMethod"] = 0, ["testContainer"] = 0, ["mcpTool"] = 0,
            ["generated"] = 0, ["composition"] = 0, ["interop"] = 0,
        };
}
