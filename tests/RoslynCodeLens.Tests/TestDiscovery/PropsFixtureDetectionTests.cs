using RoslynCodeLens.TestDiscovery;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.TestDiscovery;

/// <summary>
/// Regression tests for #406: a test project whose xunit reference comes exclusively from a
/// shared <c>tests/Directory.Build.props</c> (no test-framework <c>PackageReference</c> in the
/// csproj text) must still be recognized as a test project, end to end.
/// </summary>
[Collection("PropsSolution")]
public class PropsFixtureDetectionTests
{
    private readonly PropsSolutionFixture _fixture;

    public PropsFixtureDetectionTests(PropsSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void GetTestProjectIds_DetectsPropsDeclaredTestProject()
    {
        var ids = TestProjectDetector.GetTestProjectIds(_fixture.Loaded.Solution);

        var names = ids
            .Select(id => _fixture.Loaded.Solution.GetProject(id)!.Name)
            .ToList();

        Assert.Contains("PropsDrivenTests", names);
        Assert.DoesNotContain("PropsLib", names);
    }

    [Fact]
    public void GetTestSummary_ReportsPropsDeclaredTestProject()
    {
        var result = GetTestSummaryLogic.Execute(_fixture.Loaded, _fixture.Resolver, project: null);

        var project = Assert.Single(result.Projects);
        Assert.Equal("PropsDrivenTests", project.Project);
        Assert.Contains(project.Tests, t => t.MethodName.Contains("DescribeContainsName", StringComparison.Ordinal));
    }

    [Fact]
    public void FindUncoveredSymbols_TreatsPropsDeclaredProjectAsTests()
    {
        var result = FindUncoveredSymbolsLogic.Execute(_fixture.Loaded, _fixture.Resolver);

        // Widget.Describe is called from the [Fact], so coverage must be non-zero …
        Assert.True(result.Summary.CoveredCount > 0,
            $"expected covered symbols, got CoveredCount={result.Summary.CoveredCount}");
        Assert.DoesNotContain(result.UncoveredSymbols,
            s => string.Equals(s.Symbol, "Widget.Describe", StringComparison.Ordinal));

        // … Widget.Uncalled genuinely is not …
        Assert.Contains(result.UncoveredSymbols,
            s => string.Equals(s.Symbol, "Widget.Uncalled", StringComparison.Ordinal));

        // … and the test project's own symbols must not be listed as uncovered production code.
        Assert.DoesNotContain(result.UncoveredSymbols,
            s => string.Equals(s.Project, "PropsDrivenTests", StringComparison.Ordinal));
    }
}
