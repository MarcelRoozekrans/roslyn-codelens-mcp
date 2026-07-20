using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class RenameSymbolLogic
{
    public static async Task<RenameSymbolResult> ExecuteAsync(
        LoadedSolution loaded, SymbolResolver resolver,
        string symbol, string newName,
        bool renameOverloads, bool renameInStrings, bool renameInComments,
        bool preview, bool force,
        CommitWrittenDocuments? commitToMemory, CancellationToken ct)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{newName}' is not a valid C# identifier.", new { newName });
        }

        var target = ResolveSingleTarget(resolver, symbol);
        ValidateRenameTarget(target, symbol);

        // Degraded guard (finding 3): a load with dropped references can make Renamer miss
        // references entirely, silently producing an incomplete rename. Refuse to write in
        // that state unless the user explicitly forces it; previews warn instead (below).
        if (!preview && loaded.Degraded && !force)
        {
            return new RenameSymbolResult(false, target.ToDisplayString(), newName, Applied: false,
                [], 0, [],
                $"Refused to apply: the solution loaded degraded ({loaded.LoadDiagnostics.Count} load " +
                "diagnostic(s) — projects opened with dropped references), so the rename may be incomplete " +
                "and silently miss references. Run rebuild_solution and retry, or re-run with force=true to apply anyway.");
        }

        var options = new SymbolRenameOptions(
            RenameOverloads: renameOverloads,
            RenameInStrings: renameInStrings,
            RenameInComments: renameInComments,
            RenameFile: false);

        var renamed = await Renamer.RenameSymbolAsync(
            loaded.Solution, target, options, newName, ct).ConfigureAwait(false);

        var edits = await SolutionChangeWriter.ExtractTextEditsAsync(
            renamed, loaded.Solution, ct).ConfigureAwait(false);
        var conflicts = await ComputeConflictsAsync(loaded, renamed, ct).ConfigureAwait(false);
        var filesChanged = edits.Select(e => e.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var oldName = target.ToDisplayString();

        if (preview)
        {
            var previewMessage = conflicts.Count > 0
                ? $"{conflicts.Count} conflict(s) detected — applying would introduce new compiler errors."
                : "Preview only — no files written. Re-run with preview=false to apply.";
            if (loaded.Degraded)
            {
                previewMessage =
                    $"WARNING: the solution loaded degraded ({loaded.LoadDiagnostics.Count} load diagnostic(s) — " +
                    "projects opened with dropped references), so this rename may be incomplete and miss references. " +
                    previewMessage;
            }
            return new RenameSymbolResult(true, oldName, newName, Applied: false,
                edits, filesChanged, conflicts, previewMessage);
        }

        if (conflicts.Count > 0 && !force)
        {
            return new RenameSymbolResult(false, oldName, newName, Applied: false,
                edits, filesChanged, conflicts,
                $"Refused to apply: {conflicts.Count} new compiler error(s) would be introduced. " +
                "Inspect Conflicts, or re-run with force=true to apply anyway.");
        }

        var write = await SolutionChangeWriter.WriteChangesToDiskAsync(
            renamed, loaded.Solution, ct).ConfigureAwait(false);
        if (!write.Written)
        {
            // Freshness refusal (finding 1): something edited these files after the solution
            // snapshot was taken — writing snapshot-derived text would clobber those edits.
            return new RenameSymbolResult(false, oldName, newName, Applied: false,
                edits, filesChanged, conflicts,
                $"Refused to apply: {write.StaleFiles.Count} file(s) changed on disk after the solution " +
                $"snapshot was taken: {string.Join(", ", write.StaleFiles)}. No files were written. " +
                "Run rebuild_solution and retry.");
        }

        var message = $"Renamed {oldName} to {newName} in {filesChanged} file(s).";
        if (commitToMemory != null && write.Documents.Count > 0)
        {
            // Post-write commit (finding 5): make the in-memory snapshot reflect the new text
            // immediately instead of waiting out the file watcher's debounce window. A commit
            // failure must not fail the rename — the files ARE renamed on disk; the watcher
            // rebuild (or an explicit rebuild_solution) will converge shortly.
            try
            {
                await commitToMemory(write.Documents, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message += " Warning: files were written, but refreshing the in-memory snapshot failed " +
                    $"({ex.Message}); queries may briefly see stale text until the file watcher catches up, " +
                    "or run rebuild_solution.";
            }
        }

        return new RenameSymbolResult(true, oldName, newName, Applied: true,
            edits, filesChanged, conflicts, message);
    }

    internal static ISymbol ResolveSingleTarget(SymbolResolver resolver, string symbol)
    {
        var matches = resolver.FindSymbols(symbol);
        if (matches.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.SymbolNotFound,
                $"Symbol '{symbol}' not found.", new { symbol });
        }

        // Overloads of one method are a single rename target (Renamer handles the
        // group via RenameOverloads); everything else groups by full display string.
        var groups = LogicalMemberGroups.GroupLogicalTargets(matches);
        if (groups.Count > 1)
        {
            throw new McpToolException(ToolErrorCode.AmbiguousMatch,
                $"Symbol '{symbol}' matched {groups.Count} distinct symbols. Use a more qualified name.",
                new { matches = groups.Select(g => g.First().ToDisplayString()).ToList() });
        }

        return groups[0].First();
    }

    internal static void ValidateRenameTarget(ISymbol target, string symbol)
    {
        if (target is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor })
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{symbol}' is a constructor — rename the containing type instead; constructors follow automatically.",
                new { symbol });
        }

        if (!target.Locations.Any(l => l.IsInSource))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{symbol}' is a metadata symbol — only symbols defined in source can be renamed.",
                new { symbol });
        }
    }

    private static async Task<IReadOnlyList<RenameConflict>> ComputeConflictsAsync(
        LoadedSolution loaded, Solution renamed, CancellationToken ct)
    {
        var original = loaded.Solution;
        var conflicts = new List<RenameConflict>();
        foreach (var projectId in ComputeScanSet(original, renamed))
        {
            var afterProject = renamed.GetProject(projectId);
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
    /// Projects to conflict-scan: those Renamer textually changed, plus their
    /// direct dependents — a rename can break a downstream project without
    /// touching its text (source generators, name-based lookup changes).
    /// </summary>
    internal static IReadOnlyList<ProjectId> ComputeScanSet(Solution original, Solution renamed)
    {
        var changed = renamed.GetChanges(original).GetProjectChanges()
            .Select(c => c.ProjectId).ToList();
        var graph = original.GetProjectDependencyGraph();

        var seen = new HashSet<ProjectId>();
        var scanSet = new List<ProjectId>();
        foreach (var projectId in changed)
        {
            if (seen.Add(projectId))
                scanSet.Add(projectId);
        }
        foreach (var projectId in changed)
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
    /// no role, so pre-existing errors whose message embeds the renamed symbol
    /// don't become phantom conflicts, and a new error that duplicates an existing
    /// one's message still surfaces as a count increase. Suppressed diagnostics
    /// are skipped, matching GetDiagnosticsLogic's policy.
    /// </summary>
    internal static List<RenameConflict> DiffNewErrors(
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
