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
    /// Projects to conflict-scan: those the edit textually changed, plus their
    /// direct dependents — an edit can break a downstream project without
    /// touching its text (source generators, name-based lookup changes).
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
            foreach (var dependent in graph.GetProjectsThatDirectlyDependOnThisProject(projectId))
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
