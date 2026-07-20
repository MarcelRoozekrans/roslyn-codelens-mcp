using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens;

/// <summary>
/// Safety gates shared by every tool that rewrites the solution (rename_symbol, change_signature):
/// the degraded-load guard and the compiler-diagnostics delta that turns "this edit would introduce
/// new errors" into reportable <see cref="RenameConflict"/>s. Both tools must judge risk the same
/// way, so the logic lives here once rather than being copied per tool.
/// </summary>
public static class SolutionChangeSafety
{
    /// <summary>
    /// Apply-mode refusal message for a degraded load, or null when there is nothing to refuse.
    /// A load with dropped references can make Roslyn miss references entirely, silently producing
    /// an incomplete rewrite; writing in that state is refused unless the caller forces it.
    /// <paramref name="operation"/> names the edit in prose ("rename", "signature change").
    /// </summary>
    public static string? DegradedApplyRefusal(LoadedSolution loaded, bool force, string operation)
    {
        if (!loaded.Degraded || force)
            return null;

        return $"Refused to apply: the solution loaded degraded ({loaded.LoadDiagnostics.Count} load " +
            "diagnostic(s) — projects opened with dropped references), so the " + operation +
            " may be incomplete and silently miss references. Run rebuild_solution and retry, " +
            "or re-run with force=true to apply anyway.";
    }

    /// <summary>
    /// Prefixes a preview message with the degraded-load warning when the load was degraded;
    /// returns <paramref name="message"/> unchanged otherwise.
    /// </summary>
    public static string DegradedPreviewWarning(LoadedSolution loaded, string operation, string message)
    {
        if (!loaded.Degraded)
            return message;

        return $"WARNING: the solution loaded degraded ({loaded.LoadDiagnostics.Count} load diagnostic(s) — " +
            "projects opened with dropped references), so this " + operation +
            " may be incomplete and miss references. " + message;
    }

    /// <summary>
    /// What <see cref="PreviewOrApplyAsync"/> decided, in the vocabulary both rewriting tools
    /// share. Each tool maps this onto its own result record; nothing here is tool-specific.
    /// </summary>
    public sealed record SolutionChangeOutcome(
        bool Success,
        bool Applied,
        IReadOnlyList<TextEdit> Edits,
        int FilesChanged,
        IReadOnlyList<RenameConflict> Conflicts,
        string Message);

    /// <summary>
    /// The single decision procedure for whether bytes hit disk, shared by rename_symbol and
    /// change_signature: diff the change into edits, compute conflicts, and then either report a
    /// preview, refuse over conflicts, refuse over a stale file, or write and commit.
    /// <para>
    /// This lives here rather than in each tool because these are the steps that decide whether a
    /// user's files are modified. Two copies drift, and a drift in this sequence is the difference
    /// between refusing to clobber a concurrent edit and clobbering it. Only the success sentence
    /// varies per tool, which is what <paramref name="describeApplied"/> supplies;
    /// <paramref name="operation"/> names the edit in prose ("rename", "signature change") for the
    /// degraded-load warning.
    /// </para>
    /// </summary>
    public static async Task<SolutionChangeOutcome> PreviewOrApplyAsync(
        LoadedSolution loaded, Solution changed, string operation,
        bool preview, bool force,
        Func<int, string> describeApplied,
        CommitWrittenDocuments? commitToMemory, CancellationToken ct)
    {
        var edits = await SolutionChangeWriter.ExtractTextEditsAsync(
            changed, loaded.Solution, ct).ConfigureAwait(false);
        var conflicts = await ComputeConflictsAsync(loaded, changed, ct).ConfigureAwait(false);
        var filesChanged = edits.Select(e => e.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (preview)
        {
            var previewMessage = conflicts.Count > 0
                ? $"{conflicts.Count} conflict(s) detected — applying would introduce new compiler errors."
                : "Preview only — no files written. Re-run with preview=false to apply.";
            previewMessage = DegradedPreviewWarning(loaded, operation, previewMessage);
            return new SolutionChangeOutcome(true, false, edits, filesChanged, conflicts, previewMessage);
        }

        if (conflicts.Count > 0 && !force)
        {
            return new SolutionChangeOutcome(false, false, edits, filesChanged, conflicts,
                $"Refused to apply: {conflicts.Count} new compiler error(s) would be introduced. " +
                "Inspect Conflicts, or re-run with force=true to apply anyway.");
        }

        var write = await SolutionChangeWriter.WriteChangesToDiskAsync(
            changed, loaded.Solution, ct).ConfigureAwait(false);
        if (!write.Written)
        {
            // Freshness refusal: something edited these files after the solution snapshot was
            // taken, so writing snapshot-derived text would clobber those edits. Deliberately NOT
            // overridable by force — unlike conflicts, this is not a risk the caller can accept,
            // because the text that would be written was computed from something else entirely.
            return new SolutionChangeOutcome(false, false, edits, filesChanged, conflicts,
                $"Refused to apply: {write.StaleFiles.Count} file(s) changed on disk after the solution " +
                $"snapshot was taken: {string.Join(", ", write.StaleFiles)}. No files were written. " +
                "Run rebuild_solution and retry.");
        }

        var message = describeApplied(filesChanged);

        // Post-write commit: make the in-memory snapshot reflect the new text immediately instead
        // of waiting out the file watcher's debounce window. No outcome here — cancellation
        // included — may fail the operation: the files are already changed on disk, so reporting
        // failure would misdescribe what happened.
        var commitWarning = await SolutionChangeWriter.CommitAsync(
            commitToMemory, write, ct).ConfigureAwait(false);
        if (commitWarning != null)
            message += " Warning: " + commitWarning;

        return new SolutionChangeOutcome(true, true, edits, filesChanged, conflicts, message);
    }

    /// <summary>
    /// New compiler errors the changed solution would introduce, over the scan set
    /// (<see cref="ComputeScanSet"/>) and using the count-based delta in <see cref="DiffNewErrors"/>.
    /// </summary>
    public static async Task<IReadOnlyList<RenameConflict>> ComputeConflictsAsync(
        LoadedSolution loaded, Solution changed, CancellationToken ct)
    {
        var original = loaded.Solution;
        var conflicts = new List<RenameConflict>();
        foreach (var projectId in ComputeScanSet(original, changed))
        {
            var afterProject = changed.GetProject(projectId);
            if (afterProject == null)
                continue;

            // Before side prefers the cached compilation (already built at load
            // time); after side is the forked project. Both diagnostics passes
            // are CPU-bound, so run them concurrently.
            var beforeTask = Task.Run(async () =>
            {
                if (!loaded.Compilations.TryGetValue(projectId, out var compilation))
                {
                    compilation = await original.GetProject(projectId)!
                        .GetCompilationAsync(ct).ConfigureAwait(false);
                }
                return compilation?.GetDiagnostics(ct);
            }, ct);
            var afterTask = Task.Run(async () =>
            {
                var compilation = await afterProject.GetCompilationAsync(ct).ConfigureAwait(false);
                return compilation?.GetDiagnostics(ct);
            }, ct);
            await Task.WhenAll(beforeTask, afterTask).ConfigureAwait(false);

            var before = await beforeTask.ConfigureAwait(false);
            var after = await afterTask.ConfigureAwait(false);
            if (before == null || after == null)
                continue;

            conflicts.AddRange(DiffNewErrors(before, after));
        }
        return conflicts;
    }

    /// <summary>
    /// Projects to conflict-scan: those the edit textually changed, plus every project that
    /// depends on them TRANSITIVELY — an edit can break a downstream project without touching its
    /// text (source generators, name-based lookup changes), and a project two hops away sees the
    /// changed types just as directly as one hop away does. Scanning only immediate dependents
    /// silently missed those.
    /// <para>
    /// Cost: each scanned project outside the cached set needs a compilation, so a change low in
    /// a wide dependency graph can compile most of the solution. That is the price of not
    /// under-reporting conflicts on exactly the changes most likely to break something.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ProjectId> ComputeScanSet(Solution original, Solution changed)
    {
        var changedProjects = changed.GetChanges(original).GetProjectChanges()
            .Select(c => c.ProjectId).ToList();
        var graph = original.GetProjectDependencyGraph();

        var seen = new HashSet<ProjectId>();
        var scanSet = new List<ProjectId>();
        foreach (var projectId in changedProjects)
        {
            if (seen.Add(projectId))
                scanSet.Add(projectId);
        }
        foreach (var projectId in changedProjects)
        {
            foreach (var dependent in graph.GetProjectsThatTransitivelyDependOnThisProject(projectId))
            {
                if (seen.Add(dependent))
                    scanSet.Add(dependent);
            }
        }
        return scanSet;
    }

    /// <summary>
    /// Multiset diff of error diagnostics keyed by (Id, FilePath) — messages play
    /// no role, so pre-existing errors whose message embeds the edited symbol
    /// don't become phantom conflicts, and a new error that duplicates an existing
    /// one's message still surfaces as a count increase. Suppressed diagnostics
    /// are skipped, matching GetDiagnosticsLogic's policy.
    /// </summary>
    public static List<RenameConflict> DiffNewErrors(
        IEnumerable<Diagnostic> before, IEnumerable<Diagnostic> after)
    {
        static bool IsReportableError(Diagnostic d)
            => d.Severity == DiagnosticSeverity.Error && !d.IsSuppressed;
        static string Key(Diagnostic d)
            => $"{d.Id}|{d.Location.GetLineSpan().Path}";
        static int Line(Diagnostic d)
            => d.Location.GetLineSpan().StartLinePosition.Line + 1;

        var beforeGroups = before.Where(IsReportableError)
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var conflicts = new List<RenameConflict>();
        foreach (var group in after.Where(IsReportableError)
                     .GroupBy(Key, StringComparer.OrdinalIgnoreCase))
        {
            var afterDiags = group.ToList();
            var beforeCount = beforeGroups.TryGetValue(group.Key, out var beforeDiags)
                ? beforeDiags.Count : 0;
            var newCount = afterDiags.Count - beforeCount;
            if (newCount <= 0)
                continue;

            // Prefer diagnostics on lines the before side didn't have — those are
            // most likely the genuinely new ones; fall back to arbitrary members.
            var beforeLines = beforeDiags?.Select(Line).ToHashSet() ?? [];
            foreach (var diag in afterDiags
                         .OrderBy(d => beforeLines.Contains(Line(d)) ? 1 : 0)
                         .Take(newCount))
            {
                var span = diag.Location.GetLineSpan();
                conflicts.Add(new RenameConflict(
                    diag.Id, diag.GetMessage(), span.Path,
                    span.StartLinePosition.Line + 1));
            }
        }
        return conflicts;
    }
}
