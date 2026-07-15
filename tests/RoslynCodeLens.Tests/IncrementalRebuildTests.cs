using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Regression tests for #282: after the file watcher picked up a .cs edit and triggered its
/// incremental rebuild, every SymbolFinder-based tool (find_references, find_callers) returned
/// totalCount:0 permanently. The rebuild re-opened the solution, minting fresh ProjectIds that
/// no longer matched the retained compilations, so SymbolFinder was handed symbols foreign to
/// the solution and silently returned empty. The fix applies source edits to the existing
/// solution in place (Solution.WithDocumentText), preserving identity. These tests exercise the
/// SymbolFinder-backed tools end-to-end across the edit-then-query flow that triggered the bug.
/// </summary>
public class IncrementalRebuildTests
{
    private static string SolutionPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));

    private static string FixtureFile(params string[] parts) =>
        Path.Combine(new[] { Path.GetDirectoryName(SolutionPath)! }.Concat(parts).ToArray());

    [Fact]
    public async Task IncrementalRebuild_AfterCsEdit_FindReferencesReflectsNewUsage()
    {
        // ICrossProjectOnly is declared in TestLib and used only in TestLib2/CrossProjectGreeter.cs
        // — the exact cross-project shape from the #282 repro (a symbol from an unchanged project,
        // referenced from the edited project).
        var consumerPath = FixtureFile("TestLib2", "CrossProjectGreeter.cs");
        var original = await File.ReadAllTextAsync(consumerPath).ConfigureAwait(false);

        SolutionManager? manager = null;
        try
        {
            manager = await CreateHealthyManagerAsync().ConfigureAwait(false);
            var before = CountReferences(manager, "ICrossProjectOnly");

            // Add a second implementation of the cross-project interface — one extra reference.
            await File.WriteAllTextAsync(consumerPath, original +
                "\n\npublic class ExtraCrossConsumer : ICrossProjectOnly\n{\n\tpublic string Execute() => \"extra\";\n}\n")
                .ConfigureAwait(false);
            manager.SimulateFileChangeForTest(consumerPath);

            var after = CountReferences(manager, "ICrossProjectOnly");
            Assert.True(after > before,
                $"find_references for ICrossProjectOnly should reflect the new usage after an incremental " +
                $"rebuild (before={before}, after={after}). A count of 0 is the #282 silent-empty bug.");
        }
        finally
        {
            await File.WriteAllTextAsync(consumerPath, original).ConfigureAwait(false);
            manager?.Dispose();
        }
    }

    [Fact]
    public async Task IncrementalRebuild_AfterCsEdit_FindCallersReflectsNewCallSite()
    {
        // find_callers is the other SymbolFinder-backed tool called out in #282. GreeterConsumer
        // (TestLib2) calls IGreeter.Greet (declared in TestLib); adding a second call site must be
        // reflected after the incremental rebuild rather than collapsing to 0.
        var consumerPath = FixtureFile("TestLib2", "GreeterConsumer.cs");
        var original = await File.ReadAllTextAsync(consumerPath).ConfigureAwait(false);

        SolutionManager? manager = null;
        try
        {
            manager = await CreateHealthyManagerAsync().ConfigureAwait(false);
            var before = CountCallers(manager, "IGreeter.Greet");
            Assert.True(before > 0, "expected an existing cross-project caller of IGreeter.Greet");

            // Insert a second method that calls _greeter.Greet, before the class's closing brace.
            var idx = original.LastIndexOf('}');
            var edited = original[..idx] + "    public string SayHi() => _greeter.Greet(\"Hi\");\n" + original[idx..];
            await File.WriteAllTextAsync(consumerPath, edited).ConfigureAwait(false);
            manager.SimulateFileChangeForTest(consumerPath);

            var after = CountCallers(manager, "IGreeter.Greet");
            Assert.True(after > before,
                $"find_callers for IGreeter.Greet should reflect the new call site (before={before}, after={after}).");
        }
        finally
        {
            await File.WriteAllTextAsync(consumerPath, original).ConfigureAwait(false);
            manager?.Dispose();
        }
    }

    [Fact]
    public async Task IncrementalRebuild_EditingDefiningProject_KeepsCrossProjectReferencesResolvable()
    {
        // Editing the project that DEFINES a symbol recompiles it (new symbol identity) and, via
        // transitive staleness, its dependents too. Cross-project references must still resolve —
        // a naive rebuild that recompiled only the defining project would leave dependents bound to
        // the old symbol and drop the reference. Also verifies a second sequential edit stays sound.
        var defPath = FixtureFile("TestLib", "ICrossProjectOnly.cs");
        var original = await File.ReadAllTextAsync(defPath).ConfigureAwait(false);

        SolutionManager? manager = null;
        try
        {
            manager = await CreateHealthyManagerAsync().ConfigureAwait(false);
            var baseline = References(manager, "ICrossProjectOnly");
            Assert.Contains(baseline, r => r.File.Contains("CrossProjectGreeter", StringComparison.Ordinal));

            // Trivial semantic-preserving edit (append a comment) forces TestLib + dependents to recompile.
            await File.WriteAllTextAsync(defPath, original + "\n// touch 1\n").ConfigureAwait(false);
            manager.SimulateFileChangeForTest(defPath);

            var afterFirst = References(manager, "ICrossProjectOnly");
            Assert.Contains(afterFirst, r => r.File.Contains("CrossProjectGreeter", StringComparison.Ordinal));
            Assert.Equal(baseline.Count, afterFirst.Count);

            // Second sequential edit — the tracker re-mapped against the new snapshot; this must still work.
            await File.WriteAllTextAsync(defPath, original + "\n// touch 1\n// touch 2\n").ConfigureAwait(false);
            manager.SimulateFileChangeForTest(defPath);

            var afterSecond = References(manager, "ICrossProjectOnly");
            Assert.Contains(afterSecond, r => r.File.Contains("CrossProjectGreeter", StringComparison.Ordinal));
            Assert.Equal(baseline.Count, afterSecond.Count);
        }
        finally
        {
            await File.WriteAllTextAsync(defPath, original).ConfigureAwait(false);
            manager?.Dispose();
        }
    }

    [Fact]
    public async Task ForceReload_ConcurrentWithAutoRebuilds_NeverClobbersOrCorruptsState()
    {
        // rebuild_solution (ForceReloadAsync) and the watcher-driven auto-rebuild both produce a new
        // loaded solution. Before they were serialized, an auto-rebuild could read _loaded, have a
        // concurrent ForceReload swap in a fresh solution and dispose the old workspace, then swap its
        // own result back — discarding the reload and leaving _loaded forked off a disposed workspace.
        // With the shared gate every query stays internally consistent under contention: the invariant
        // "the cross-project reference always resolves and nothing throws" holds regardless of timing.
        var consumerPath = FixtureFile("TestLib2", "CrossProjectGreeter.cs");

        SolutionManager? manager = null;
        try
        {
            manager = await CreateHealthyManagerAsync().ConfigureAwait(false);
            var mgr = manager;

            var stop = false;
            Exception? readerError = null;
            var minRefs = int.MaxValue;

            // Reader thread: hammer auto-rebuilds (mark stale) + queries while the reloads run.
            var reader = Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        mgr.SimulateFileChangeForTest(consumerPath);
                        var refs = CountReferences(mgr, "ICrossProjectOnly");
                        minRefs = Math.Min(minRefs, refs);
                    }
                }
                catch (Exception ex) { readerError = ex; }
            });

            for (var i = 0; i < 2; i++)
                await mgr.ForceReloadAsync().ConfigureAwait(false);

            Volatile.Write(ref stop, true);
            await reader.ConfigureAwait(false);

            Assert.Null(readerError); // a clobber/disposed-workspace access would surface here
            Assert.True(minRefs > 0,
                $"every concurrent query must resolve the cross-project reference (min observed={minRefs}).");
        }
        finally
        {
            manager?.Dispose();
        }
    }

    private static IReadOnlyList<SymbolReference> References(SolutionManager manager, string symbol)
    {
        var (loaded, resolver, metadata) = manager.GetAnalysisContext();
        return FindReferencesLogic.Execute(loaded, resolver, metadata, symbol);
    }

    private static int CountReferences(SolutionManager manager, string symbol) => References(manager, symbol).Count;

    private static int CountCallers(SolutionManager manager, string symbol)
    {
        var (loaded, resolver, metadata) = manager.GetAnalysisContext();
        return FindCallersLogic.Execute(loaded, resolver, metadata, symbol).Count;
    }

    /// <summary>
    /// Creates a manager, waits for warmup, and probes the cross-project reference path until it is
    /// healthy — retrying on the MSBuildWorkspace design-time-build reference-drop flake (#260) so a
    /// degraded load never masquerades as a real result.
    /// </summary>
    private static async Task<SolutionManager> CreateHealthyManagerAsync()
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var manager = await SolutionManager.CreateAsync(SolutionPath).ConfigureAwait(false);
            await manager.WaitForWarmupAsync().ConfigureAwait(false);

            var (loaded, resolver, metadata) = manager.GetAnalysisContext();
            if (!loaded.Degraded &&
                FindReferencesLogic.Execute(loaded, resolver, metadata, "ICrossProjectOnly").Count > 0)
            {
                return manager;
            }

            manager.Dispose();
        }

        throw new InvalidOperationException(
            $"TestSolution failed to load healthily after {maxAttempts} attempts (cross-project references for " +
            "ICrossProjectOnly came back empty/degraded — the #260 MSBuildWorkspace reference-drop flake).");
    }
}
