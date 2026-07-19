using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class RenameSymbolLogic
{
    public static async Task<RenameSymbolResult> ExecuteAsync(
        LoadedSolution loaded, SymbolResolver resolver,
        string symbol, string newName,
        bool renameOverloads, bool renameInStrings, bool renameInComments,
        bool preview, bool force, CancellationToken ct)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{newName}' is not a valid C# identifier.", new { newName });
        }

        var target = ResolveSingleTarget(resolver, symbol);
        ValidateRenameTarget(target, symbol);

        var options = new SymbolRenameOptions(
            RenameOverloads: renameOverloads,
            RenameInStrings: renameInStrings,
            RenameInComments: renameInComments,
            RenameFile: false);

        var renamed = await Renamer.RenameSymbolAsync(
            loaded.Solution, target, options, newName, ct).ConfigureAwait(false);

        var edits = await SolutionChangeWriter.ExtractTextEditsAsync(
            renamed, loaded.Solution, ct).ConfigureAwait(false);
        var conflicts = await ComputeConflictsAsync(loaded.Solution, renamed, ct).ConfigureAwait(false);
        var filesChanged = edits.Select(e => e.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var oldName = target.ToDisplayString();

        if (preview)
        {
            return new RenameSymbolResult(true, oldName, newName, Applied: false,
                edits, filesChanged, conflicts,
                conflicts.Count > 0
                    ? $"{conflicts.Count} conflict(s) detected — applying would introduce new compiler errors."
                    : "Preview only — no files written. Re-run with preview=false to apply.");
        }

        if (conflicts.Count > 0 && !force)
        {
            return new RenameSymbolResult(false, oldName, newName, Applied: false,
                edits, filesChanged, conflicts,
                $"Refused to apply: {conflicts.Count} new compiler error(s) would be introduced. " +
                "Inspect Conflicts, or re-run with force=true to apply anyway.");
        }

        await SolutionChangeWriter.WriteChangesToDiskAsync(
            renamed, loaded.Solution, ct).ConfigureAwait(false);
        return new RenameSymbolResult(true, oldName, newName, Applied: true,
            edits, filesChanged, conflicts,
            $"Renamed {oldName} to {newName} in {filesChanged} file(s).");
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
        var groups = matches.GroupBy(GroupKey).ToList();
        if (groups.Count > 1)
        {
            throw new McpToolException(ToolErrorCode.AmbiguousMatch,
                $"Symbol '{symbol}' matched {groups.Count} distinct symbols. Use a more qualified name.",
                new { matches = groups.Select(g => g.First().ToDisplayString()).ToList() });
        }

        return groups[0].First();
    }

    private static object GroupKey(ISymbol s) => s is IMethodSymbol m
        ? (m.ContainingType?.ToDisplayString() ?? "", m.Name)
        : s.ToDisplayString();

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
        Solution original, Solution renamed, CancellationToken ct)
    {
        var conflicts = new List<RenameConflict>();
        foreach (var change in renamed.GetChanges(original).GetProjectChanges())
        {
            var before = await change.OldProject.GetCompilationAsync(ct).ConfigureAwait(false);
            var after = await change.NewProject.GetCompilationAsync(ct).ConfigureAwait(false);
            if (before == null || after == null)
                continue;

            var beforeKeys = before.GetDiagnostics(ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(DiagnosticKey)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var diag in after.GetDiagnostics(ct)
                         .Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                if (beforeKeys.Contains(DiagnosticKey(diag)))
                    continue;

                var span = diag.Location.GetLineSpan();
                conflicts.Add(new RenameConflict(
                    diag.Id, diag.GetMessage(), span.Path,
                    span.StartLinePosition.Line + 1));
            }
        }
        return conflicts;
    }

    private static string DiagnosticKey(Diagnostic d)
        => $"{d.Id}|{d.Location.GetLineSpan().Path}|{d.GetMessage()}";
}
