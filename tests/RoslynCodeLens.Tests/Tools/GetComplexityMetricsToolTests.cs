using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetComplexityMetricsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetComplexityMetricsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void GetComplexityMetrics_AllMethods_ReturnsResults()
    {
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void GetComplexityMetrics_HighThreshold_ReturnsEmpty()
    {
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 100);
        Assert.Empty(results);
    }

    [Fact]
    public void GetComplexityMetrics_ProjectFilter_FiltersResults()
    {
        _ = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);
        var filtered = GetComplexityMetricsLogic.Execute(_loaded, _resolver, "TestLib2", 0);
        Assert.All(filtered, r => Assert.Equal("TestLib2", r.Project));
    }

    [Fact]
    public void Sort_OrdersByComplexityDesc()
    {
        var input = new List<ComplexityMetric>
        {
            new("low",  "T", 3,  "a.cs", 1, "P"),
            new("high", "T", 20, "b.cs", 1, "P"),
            new("mid",  "T", 10, "c.cs", 1, "P"),
        };

        var sorted = GetComplexityMetricsTool.Sort(input);

        Assert.Collection(sorted,
            m => Assert.Equal("high", m.MethodName),
            m => Assert.Equal("mid",  m.MethodName),
            m => Assert.Equal("low",  m.MethodName));
    }

    [Fact]
    public void BuildSummary_ReportsMaxAvgOverThreshold()
    {
        var input = new List<ComplexityMetric>
        {
            new("a", "T", 5,  "a.cs", 1, "P"),
            new("b", "T", 15, "a.cs", 2, "P"),
            new("c", "T", 25, "a.cs", 3, "P"),
        };

        var summary = GetComplexityMetricsTool.BuildSummary(input, threshold: 10);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"max\":25", json, StringComparison.Ordinal);
        Assert.Contains("\"avg\":15", json, StringComparison.Ordinal);
        Assert.Contains("\"overThreshold\":2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSummary_EmptyList_ReturnsZeros()
    {
        var summary = GetComplexityMetricsTool.BuildSummary(Array.Empty<ComplexityMetric>(), threshold: 10);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"max\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"overThreshold\":0", json, StringComparison.Ordinal);
    }
}
