using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Guards a performance invariant that is otherwise invisible to tests.
/// <para>
/// <c>check_architecture</c> decides whether a tree can possibly violate any rule by reading its
/// declared namespaces, and skips it <i>before</i> a semantic model is built. That filter is the
/// whole reason its cost tracks the rules written rather than the size of the solution. Because
/// <see cref="RoslynCodeLens.Analysis.SolutionScanner"/> hands out the model as a lazy factory, the
/// invariant holds only while the logic never reaches for a model by itself — and nothing else
/// would fail if someone added a direct call tomorrow. The tool would stay correct and quietly
/// become O(solution).
/// </para>
/// <para>
/// Asserted structurally rather than by counting invocations at runtime: the factory is created
/// inside the scanner, so a counting seam would mean adding a test-only parameter to production
/// code — a worse trade than parsing the source.
/// </para>
/// </summary>
public class SemanticModelLazinessTests
{
    private static string SourcePath(string relative)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(root, "src", "RoslynCodeLens", relative);
    }

    [Theory]
    // The scanner owns model creation; every scanning tool must go through its lazy accessor.
    [InlineData("Tools/CheckArchitectureLogic.cs")]
    [InlineData("Tools/FindThrowSitesLogic.cs")]
    [InlineData("Tools/FindCatchBlocksLogic.cs")]
    // Verified non-vacuous: adding Analysis/SolutionScanner.cs here — the one file that legitimately
    // creates models — makes this test fail, so the detector really does fire.
    public void ScanningTools_NeverCreateASemanticModelDirectly(string relativePath)
    {
        var path = SourcePath(relativePath);
        Assert.True(File.Exists(path), $"Expected to find {path}. Fix the test's path arithmetic, not the assertion.");

        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();

        var direct = root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(m => string.Equals(m.Name.Identifier.Text, "GetSemanticModel", StringComparison.Ordinal))
            .Select(m => m.ToString())
            .ToList();

        Assert.True(direct.Count == 0,
            $"{relativePath} calls GetSemanticModel directly ({string.Join(", ", direct)}). " +
            "Scanning tools must use SolutionScanner's lazy accessor: creating a model eagerly " +
            "undoes the pre-model filtering that keeps these scans proportional to their input.");
    }
}
