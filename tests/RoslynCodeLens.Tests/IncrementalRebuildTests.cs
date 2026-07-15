using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Regression tests for #282: after the file watcher picked up a .cs edit and triggered its
/// incremental rebuild, every SymbolFinder-based tool (find_references, find_callers) returned
/// totalCount:0 permanently. The rebuild re-opened the solution, minting fresh ProjectIds that
/// no longer matched the retained compilations, so SymbolFinder was handed symbols foreign to
/// the solution and silently returned empty. The fix applies source edits to the existing
/// solution in place (Solution.WithDocumentText), preserving identity.
/// </summary>
public class IncrementalRebuildTests
{
    private static string SolutionPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));

    [Fact]
    public async Task IncrementalRebuild_AfterCsEdit_FindReferencesReflectsNewUsage()
    {
        // ICrossProjectOnly is declared in TestLib and used only in TestLib2/CrossProjectGreeter.cs
        // — the exact cross-project shape from the #282 repro (a symbol from an unchanged project,
        // referenced from the edited project).
        var solutionDir = Path.GetDirectoryName(SolutionPath)!;
        var consumerPath = Path.Combine(solutionDir, "TestLib2", "CrossProjectGreeter.cs");
        var original = await File.ReadAllTextAsync(consumerPath).ConfigureAwait(false);

        SolutionManager? manager = null;
        try
        {
            int before;
            (manager, before) = await CreateManagerWithBaselineAsync().ConfigureAwait(false);

            // Edit the consumer on disk, adding a second implementation of the cross-project
            // interface — one extra reference. (using TestLib; is already in the file.)
            var edited = original +
                "\n\npublic class ExtraCrossConsumer : ICrossProjectOnly\n{\n\tpublic string Execute() => \"extra\";\n}\n";
            await File.WriteAllTextAsync(consumerPath, edited).ConfigureAwait(false);

            manager.SimulateFileChangeForTest(consumerPath);

            var (loaded, resolver, metadata) = manager.GetAnalysisContext();
            var after = FindReferencesLogic.Execute(loaded, resolver, metadata, "ICrossProjectOnly");

            Assert.True(
                after.Count > before,
                $"find_references for ICrossProjectOnly should reflect the new usage after an incremental " +
                $"rebuild (before={before}, after={after.Count}). A count of 0 is the #282 silent-empty bug.");
            Assert.Contains(after, r => r.File.Contains("CrossProjectGreeter", StringComparison.Ordinal));
        }
        finally
        {
            await File.WriteAllTextAsync(consumerPath, original).ConfigureAwait(false);
            manager?.Dispose();
        }
    }

    /// <summary>
    /// Creates a manager and returns the baseline find_references count for ICrossProjectOnly.
    /// Retries on the MSBuildWorkspace design-time-build reference-drop flake (#260) so the
    /// baseline is never a degraded zero.
    /// </summary>
    private static async Task<(SolutionManager Manager, int Baseline)> CreateManagerWithBaselineAsync()
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var manager = await SolutionManager.CreateAsync(SolutionPath).ConfigureAwait(false);
            await manager.WaitForWarmupAsync().ConfigureAwait(false);

            var (loaded, resolver, metadata) = manager.GetAnalysisContext();
            if (!loaded.Degraded)
            {
                var baseline = FindReferencesLogic.Execute(loaded, resolver, metadata, "ICrossProjectOnly");
                if (baseline.Count > 0)
                    return (manager, baseline.Count);
            }

            manager.Dispose();
        }

        throw new InvalidOperationException(
            $"TestSolution failed to load healthily after {maxAttempts} attempts (baseline references for " +
            "ICrossProjectOnly came back empty/degraded — the #260 MSBuildWorkspace reference-drop flake).");
    }
}
