using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Read-only checks against the real TestSolution. XUnitFixture genuinely references
/// TestLib (SampleTests news up TestLib.Greeter) and genuinely does not touch TestLib2.
/// </summary>
[Collection("TestSolution")]
public class CheckArchitectureFixtureTests
{
    private readonly TestSolutionFixture _fixture;
    public CheckArchitectureFixtureTests(TestSolutionFixture fixture) => _fixture = fixture;

    private IReadOnlyList<ArchitectureViolation> Run(IReadOnlyList<ArchitectureRule> rules, string scope = "namespace")
        => CheckArchitectureLogic.Execute(_fixture.Loaded, _fixture.Resolver, rules, scope, maxSitesPerViolation: 5);

    [Fact]
    public void ForbidARelationshipTheSolutionDoesNotHave_IsClean()
        => Assert.Empty(Run([new ArchitectureRule("forbid", "XUnitFixture", ["TestLib2.*"])]));

    [Fact]
    public void AllowOnlyWithAnImplausibleAllowList_CatchesTheRealDependency()
    {
        var violations = Run([new ArchitectureRule("allowOnly", "XUnitFixture", ["Demo.Nothing.AtAll"])]);

        var toTestLib = Assert.Single(violations, v => string.Equals(v.TargetScope, "TestLib", StringComparison.Ordinal));
        Assert.Equal("XUnitFixture", toTestLib.SourceScope);
        Assert.True(toTestLib.ReferenceCount >= 1);
        Assert.NotEmpty(toTestLib.Sites);
        Assert.EndsWith("SampleTests.cs", toTestLib.Sites[0].File, StringComparison.OrdinalIgnoreCase);

        // The key semantic on a real solution: xunit's `Fact`/`Assert` live in metadata, so
        // allowOnly must not report them despite not being on the allow-list.
        Assert.DoesNotContain(violations, v => v.TargetScope.StartsWith("Xunit", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.TargetScope.StartsWith("System", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectScope_SeesTheFixtureToTestLibEdge()
    {
        var violation = Assert.Single(
            Run([new ArchitectureRule("forbid", "XUnitFixture", ["TestLib"])], scope: "project"));

        Assert.Equal("XUnitFixture", violation.SourceScope);
        Assert.Equal("TestLib", violation.TargetScope);
    }

    [Fact]
    public void ToolWrapper_ProducesSummaryAndEnvelope()
    {
        var rules = new[] { new ArchitectureRule("forbid", "XUnitFixture", ["TestLib"]) };
        var raw = Run(rules);

        var result = CheckArchitectureTool.BuildResult(raw, rules.Length, limit: 100);

        Assert.Equal(raw.Count, result.TotalCount);
        Assert.False(result.Truncated);
        Assert.NotNull(result.Summary);
    }
}
