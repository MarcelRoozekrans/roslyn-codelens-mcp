using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Unit tests for how <see cref="FileChangeTracker"/> classifies a change (incremental document
/// edit vs. structural full-reload) and for its drain/restore ownership semantics. The drain tests
/// pin the fix for the lost-update bug: a blanket <c>ClearStale</c> after a (possibly multi-second)
/// rebuild silently dropped any save made while the rebuild ran — the same silent-staleness class
/// as #282. Uses the shared solution so no extra MSBuild load is paid.
/// </summary>
[Collection("TestSolution")]
public class FileChangeTrackerClassificationTests
{
    private readonly LoadedSolution _loaded;
    private readonly string _solutionPath;

    public FileChangeTrackerClassificationTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _solutionPath = fixture.SolutionPath;
    }

    private Project ProjectNamed(string name) =>
        _loaded.Solution.Projects.First(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private string DocPath(string projectName, string endsWith) =>
        ProjectNamed(projectName).Documents
            .First(d => d.FilePath != null && d.FilePath.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase))
            .FilePath!;

    private static bool PathEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void CsEdit_IsIncrementalDocumentChange_NotFullReload()
    {
        using var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var proj = ProjectNamed("TestLib2");
        var doc = DocPath("TestLib2", "CrossProjectGreeter.cs");

        tracker.NotifyChangedPathForTest(doc);

        var snap = tracker.DrainStale();
        Assert.False(snap.RequiresFullReload);
        Assert.Contains(proj.Id, snap.StaleProjectIds);
        Assert.Contains(snap.ChangedDocumentPaths, p => PathEq(p, doc));
    }

    [Fact]
    public void ProjectFileEdit_RequiresFullReload_AndIsNotAnIncrementalDocEdit()
    {
        using var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var proj = ProjectNamed("TestLib2");

        tracker.NotifyChangedPathForTest(proj.FilePath!);

        var snap = tracker.DrainStale();
        Assert.True(snap.RequiresFullReload);
        Assert.Contains(proj.Id, snap.StaleProjectIds);
        Assert.Empty(snap.ChangedDocumentPaths);
    }

    [Fact]
    public void UnknownFile_RequiresFullReload_AndMarksEveryProjectStale()
    {
        using var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var newFile = Path.Combine(Path.GetDirectoryName(_solutionPath)!, "TestLib", "BrandNewFile.cs");

        tracker.NotifyChangedPathForTest(newFile);

        var snap = tracker.DrainStale();
        Assert.True(snap.RequiresFullReload);
        Assert.Empty(snap.ChangedDocumentPaths);
        foreach (var p in _loaded.Solution.Projects)
            Assert.Contains(p.Id, snap.StaleProjectIds);
    }

    [Fact]
    public void DllChange_IsIgnoredForStaleness()
    {
        using var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var dll = Path.Combine(Path.GetDirectoryName(_solutionPath)!, "TestLib2", "bin", "Debug", "net10.0", "TestLib2.dll");

        tracker.NotifyChangedPathForTest(dll);

        Assert.False(tracker.HasStaleProjects);
        var snap = tracker.DrainStale();
        Assert.False(snap.RequiresFullReload);
        Assert.Empty(snap.StaleProjectIds);
    }

    [Fact]
    public void CsEditInDependency_MarksDependentsStaleTransitively()
    {
        using var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var lib = ProjectNamed("TestLib");
        var lib2 = ProjectNamed("TestLib2"); // references TestLib
        var doc = DocPath("TestLib", "ICrossProjectOnly.cs");

        tracker.NotifyChangedPathForTest(doc);

        var snap = tracker.DrainStale();
        Assert.Contains(lib.Id, snap.StaleProjectIds);
        Assert.Contains(lib2.Id, snap.StaleProjectIds);
    }

    [Fact]
    public void DrainStale_ClearsLiveState_SoAnEditDuringRebuildIsNotLost()
    {
        // The lost-update fix: after a rebuild drains the stale state, an edit that arrives while
        // that rebuild is still running must remain tracked (and be picked up by the next rebuild),
        // not be wiped when the rebuild completes.
        using var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var a = DocPath("TestLib2", "CrossProjectGreeter.cs");
        var b = DocPath("TestLib2", "GreeterConsumer.cs");

        tracker.NotifyChangedPathForTest(a);        // edit A, before the rebuild
        var first = tracker.DrainStale();           // rebuild starts -> drains A, resets live state
        Assert.Contains(first.ChangedDocumentPaths, p => PathEq(p, a));
        Assert.False(tracker.HasStaleProjects);

        tracker.NotifyChangedPathForTest(b);        // edit B lands DURING the rebuild
        Assert.True(tracker.HasStaleProjects);      // ... and survives

        var second = tracker.DrainStale();
        Assert.Contains(second.ChangedDocumentPaths, p => PathEq(p, b));
        Assert.DoesNotContain(second.ChangedDocumentPaths, p => PathEq(p, a));
    }

    [Fact]
    public void RestoreStale_ReArmsAfterFailure_UnionedWithNewChanges()
    {
        using var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var a = DocPath("TestLib2", "CrossProjectGreeter.cs");
        var b = DocPath("TestLib2", "GreeterConsumer.cs");

        tracker.NotifyChangedPathForTest(a);
        var drained = tracker.DrainStale();  // rebuild starts
        tracker.NotifyChangedPathForTest(b); // edit during rebuild
        tracker.RestoreStale(drained);       // rebuild FAILED -> restore the drained work

        var snap = tracker.DrainStale();
        Assert.Contains(snap.ChangedDocumentPaths, p => PathEq(p, a)); // failed work retried
        Assert.Contains(snap.ChangedDocumentPaths, p => PathEq(p, b)); // new edit preserved
    }

    [Fact]
    public void MixedChange_ProjectFilePlusSource_RequiresFullReload()
    {
        using var tracker = new FileChangeTracker(_loaded, _solutionPath);

        tracker.NotifyChangedPathForTest(DocPath("TestLib2", "CrossProjectGreeter.cs"));
        tracker.NotifyChangedPathForTest(ProjectNamed("TestLib2").FilePath!);

        // Any structural change in the batch wins: the whole rebuild must be a full reload.
        Assert.True(tracker.DrainStale().RequiresFullReload);
    }
}
