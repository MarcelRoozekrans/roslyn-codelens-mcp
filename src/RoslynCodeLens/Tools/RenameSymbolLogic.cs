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
        var degradedRefusal = preview
            ? null
            : SolutionChangeSafety.DegradedApplyRefusal(loaded, force, "rename");
        if (degradedRefusal != null)
        {
            return new RenameSymbolResult(false, target.ToDisplayString(), newName, Applied: false,
                [], 0, [], degradedRefusal);
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
        var conflicts = await SolutionChangeSafety.ComputeConflictsAsync(
            loaded, renamed, ct).ConfigureAwait(false);
        var filesChanged = edits.Select(e => e.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var oldName = target.ToDisplayString();

        if (preview)
        {
            var previewMessage = conflicts.Count > 0
                ? $"{conflicts.Count} conflict(s) detected — applying would introduce new compiler errors."
                : "Preview only — no files written. Re-run with preview=false to apply.";
            previewMessage = SolutionChangeSafety.DegradedPreviewWarning(loaded, "rename", previewMessage);
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
        // Post-write commit: make the in-memory snapshot reflect the new text immediately instead
        // of waiting out the file watcher's debounce window. Shared with apply_code_action, and
        // like it, no outcome here — cancellation included — may fail the rename: the files are
        // already renamed on disk, so reporting failure would misdescribe what happened.
        var commitWarning = await SolutionChangeWriter.CommitAsync(commitToMemory, write, ct).ConfigureAwait(false);
        if (commitWarning != null)
            message += " Warning: " + commitWarning;

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
}
