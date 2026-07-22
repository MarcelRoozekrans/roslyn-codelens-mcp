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
    public void Execute_ReportsConstructorsAndProperties()
    {
        // Only MethodDeclarationSyntax was visited, so a complex constructor or property
        // was invisible however bad it was.
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);

        // A constructor is named after its type, matching how the other tools render one.
        Assert.Contains(results, r => r.MethodName == "OrderService" && r.TypeName == "OrderService");
        Assert.Contains(results, r => r.MethodName == "FormalNameLength");
    }

    [Fact]
    public void Execute_ReportsCognitiveAndMaxNestingAlongsideCyclomatic()
    {
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);

        var classify = results.Single(r => r.MethodName == "ClassifyName");
        // Four flat guard clauses: cyclomatic 1 + 4, cognitive 0 + 4, deepest nesting 1.
        // The three numbers disagree, so this cannot pass with the fields cross-wired.
        Assert.Equal(5, classify.Complexity);
        Assert.Equal(4, classify.Cognitive);
        Assert.Equal(1, classify.MaxNesting);
    }

    [Fact]
    public void Execute_MetricSelectsWhatThresholdFiltersOn()
    {
        // Validator.SumPositives is cyclomatic 4 but cognitive 6 — the two metrics disagree
        // across a threshold of 5, so this one pair pins both the filtering and the selection.
        var byCognitive = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 5, ComplexityMetricKind.Cognitive);
        var byCyclomatic = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 5, ComplexityMetricKind.Cyclomatic);

        Assert.Contains(byCognitive, r => r.MethodName == "SumPositives");
        Assert.DoesNotContain(byCyclomatic, r => r.MethodName == "SumPositives");
    }

    [Fact]
    public void Execute_DefaultsToCyclomatic()
    {
        // get_project_health and every other existing caller must be unaffected by the new
        // parameter, so omitting it has to behave exactly like asking for cyclomatic.
        var defaulted = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 5);
        var explicitly = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 5, ComplexityMetricKind.Cyclomatic);

        Assert.Equal(explicitly, defaulted);
    }

    [Fact]
    public void Sort_ByCognitive_OrdersByCognitiveDesc()
    {
        var input = new List<ComplexityMetric>
        {
            new("worstCyclomatic", "T", 20, 1, 1, "a.cs", 1, "P"),
            new("worstCognitive",  "T", 3, 19, 4, "b.cs", 1, "P"),
        };

        var sorted = GetComplexityMetricsTool.Sort(input, ComplexityMetricKind.Cognitive);

        Assert.Equal("worstCognitive", sorted[0].MethodName);
    }

    [Fact]
    public void BuildSummary_ByCognitive_SummarisesCognitiveAndStillReportsMaxCognitive()
    {
        var input = new List<ComplexityMetric>
        {
            new("a", "T", 30, 5,  1, "a.cs", 1, "P"),
            new("b", "T", 30, 15, 3, "a.cs", 2, "P"),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            GetComplexityMetricsTool.BuildSummary(input, threshold: 10, ComplexityMetricKind.Cognitive));

        Assert.Contains("\"max\":15", json, StringComparison.Ordinal);          // cognitive, not 30
        Assert.Contains("\"overThreshold\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"maxCognitive\":15", json, StringComparison.Ordinal); // always visible
    }

    [Fact]
    public void Sort_OrdersByComplexityDesc()
    {
        var input = new List<ComplexityMetric>
        {
            new("low",  "T", 3,  2, 1, "a.cs", 1, "P"),
            new("high", "T", 20, 19, 4, "b.cs", 1, "P"),
            new("mid",  "T", 10, 9, 2, "c.cs", 1, "P"),
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
            new("a", "T", 5,  4, 1, "a.cs", 1, "P"),
            new("b", "T", 15, 14, 3, "a.cs", 2, "P"),
            new("c", "T", 25, 24, 5, "a.cs", 3, "P"),
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
