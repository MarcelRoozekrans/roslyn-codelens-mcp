using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class RenameSymbolTool
{
    [McpServerTool(Name = "rename_symbol"),
     Description("Safely rename a type or member across the entire solution (Roslyn Renamer). " +
                 "Cascades to references, constructors, overrides, nameof, and XML doc crefs. " +
                 "Defaults to preview mode (returns edits without writing files); set preview=false to apply. " +
                 "New compiler errors the rename would introduce are reported as Conflicts, and apply mode " +
                 "refuses to write them unless force=true. Locals/parameters and file renames are not supported.")]
    public static async Task<RenameSymbolResult> Execute(
        MultiSolutionManager manager,
        [Description("Symbol to rename: simple type (MyClass), fully qualified (Namespace.MyClass), or member (MyClass.MyMethod)")] string symbol,
        [Description("New name — a bare C# identifier, e.g. 'OrderProcessor'")] string newName,
        [Description("Rename all overloads of a method together (default: true; false renames a single arbitrary overload)")] bool renameOverloads = true,
        [Description("Also rewrite occurrences inside string literals (default: false)")] bool renameInStrings = false,
        [Description("Also rewrite occurrences inside comments (default: true)")] bool renameInComments = true,
        [Description("Preview only — return edits without writing to disk (default: true)")] bool preview = true,
        [Description("Apply even when Conflicts are reported (default: false)")] bool force = false,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return await RenameSymbolLogic.ExecuteAsync(
            context.Loaded, context.Resolver, symbol, newName,
            renameOverloads, renameInStrings, renameInComments, preview, force, ct).ConfigureAwait(false);
    }
}
