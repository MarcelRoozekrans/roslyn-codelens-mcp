using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynCodeLens.Analyzers;

namespace RoslynCodeLens.Tests.Analyzers;

/// <summary>
/// Issue #399. Roslyn's <see cref="AnalyzerFileReference"/> silently refuses any analyzer built
/// against a NEWER Microsoft.CodeAnalysis than the host: it raises AnalyzerLoadFailed with
/// ErrorCode ReferencesNewerCompiler, hands back zero generators, and reports nothing. The Razor
/// generator hit exactly that, so a Blazor solution that builds clean reported phantom compile
/// errors. These tests cover the metadata-only pre-check that turns that silence into a load
/// diagnostic.
/// </summary>
public class AnalyzerCompilerVersionCheckTests
{
    private static readonly string s_analyzerLikeDll = typeof(CSharpSyntaxTree).Assembly.Location;

    [Fact]
    public void ReadRequiredCompilerVersion_ReturnsReferencedRoslynVersion()
    {
        // Microsoft.CodeAnalysis.CSharp.dll references Microsoft.CodeAnalysis, exactly as a real
        // analyzer assembly does.
        var version = AnalyzerCompilerVersionCheck.ReadRequiredCompilerVersion(s_analyzerLikeDll);

        Assert.Equal(AnalyzerCompilerVersionCheck.RunningCompilerVersion, version);
    }

    [Fact]
    public void ReadRequiredCompilerVersion_ReturnsNull_WhenAssemblyDoesNotReferenceRoslyn()
    {
        var version = AnalyzerCompilerVersionCheck.ReadRequiredCompilerVersion(typeof(object).Assembly.Location);

        Assert.Null(version);
    }

    [Fact]
    public void ReadRequiredCompilerVersion_ReturnsNull_ForMissingOrUnreadableFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), "rcl-not-here-" + Guid.NewGuid().ToString("N") + ".dll");

        Assert.Null(AnalyzerCompilerVersionCheck.ReadRequiredCompilerVersion(missing));
    }

    [Fact]
    public void FindSkewedAnalyzers_ReportsAnalyzerBuiltAgainstNewerCompiler()
    {
        var solution = SolutionWithAnalyzer(s_analyzerLikeDll);

        // Pretend the host runs an ancient Roslyn, so the real analyzer DLL is "newer".
        var skew = AnalyzerCompilerVersionCheck.FindSkewedAnalyzers(solution, new Version(1, 0, 0, 0));

        var message = Assert.Single(skew);
        Assert.Contains("Microsoft.CodeAnalysis.CSharp", message, StringComparison.Ordinal);
        Assert.Contains(AnalyzerCompilerVersionCheck.RunningCompilerVersion.ToString(), message, StringComparison.Ordinal);
        Assert.Contains("1.0.0.0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindSkewedAnalyzers_IsSilent_WhenHostCompilerIsNewEnough()
    {
        var solution = SolutionWithAnalyzer(s_analyzerLikeDll);

        var skew = AnalyzerCompilerVersionCheck.FindSkewedAnalyzers(solution, new Version(999, 0, 0, 0));

        Assert.Empty(skew);
    }

    [Fact]
    public void FindSkewedAnalyzers_ReportsEachAnalyzerOnce_AcrossProjects()
    {
        var solution = SolutionWithAnalyzer(s_analyzerLikeDll, projectCount: 3);

        var skew = AnalyzerCompilerVersionCheck.FindSkewedAnalyzers(solution, new Version(1, 0, 0, 0));

        var message = Assert.Single(skew);
        Assert.Contains("3 project(s)", message, StringComparison.Ordinal);
    }

    private static Solution SolutionWithAnalyzer(string analyzerPath, int projectCount = 1)
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        using var loader = new ShadowCopyAnalyzerAssemblyLoader(cache);
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;

        for (var i = 0; i < projectCount; i++)
        {
            var projectId = ProjectId.CreateNewId();
            solution = solution
                .AddProject(projectId, $"Proj{i}", $"Proj{i}", LanguageNames.CSharp)
                .AddAnalyzerReference(projectId, new AnalyzerFileReference(analyzerPath, loader));
        }

        return solution;
    }
}
