using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ApplyCodeActionTool
{
    [McpServerTool(Name = "apply_code_action"),
     Description("Apply a code action (refactoring or fix) by its title. " +
                 "Use get_code_actions first to discover available actions. " +
                 "Defaults to preview mode (returns diff without writing files). " +
                 "Set preview=false to apply changes to disk.")]
    public static async Task<CodeActionResult> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")] int column,
        [Description("Exact title of the code action to apply (from get_code_actions)")] string actionTitle,
        [Description("End line for text selection (1-based, optional)")] int? endLine = null,
        [Description("End column for text selection (1-based, optional)")] int? endColumn = null,
        [Description("Preview only — return diff without writing to disk (default: true)")] bool preview = true,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await ApplyCodeActionLogic.ExecuteAsync(
            manager.GetLoadedSolution(), filePath, line, column,
            endLine, endColumn, actionTitle, preview,
            // After a successful apply, push the written texts into the in-memory snapshot so
            // follow-up queries see them immediately (no watcher-debounce stale window).
            manager.CommitDocumentTextsAsync, ct).ConfigureAwait(false);
    }
}
