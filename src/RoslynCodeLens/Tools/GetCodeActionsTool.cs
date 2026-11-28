using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetCodeActionsTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "get_code_actions"),
     Description("List available code actions (refactorings and fixes) at a position in a C# file. " +
                 "Optionally specify endLine/endColumn to select a range for extract-method style refactorings. " +
                 "Returns action titles that can be passed to apply_code_action. " +
                 "Returns an envelope with items sorted by kind then title, totalCount, truncated, and limit (default 100).")]
    public static async Task<ToolListResult<CodeActionInfo>> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")] int column,
        [Description("End line for text selection (1-based, optional)")] int? endLine = null,
        [Description("End column for text selection (1-based, optional)")] int? endColumn = null,
        [Description("Maximum number of items to return (default: 100). Items are sorted by kind then title.")]
            int? limit = null,
        [Description(SolutionArgument.Description)] string? solution = null,
        CancellationToken ct = default)
    {
        var sm = manager.Resolve(solution);
        sm.EnsureLoaded();
        var raw = await GetCodeActionsLogic.ExecuteAsync(
            sm.GetLoadedSolution(), filePath, line, column,
            endLine, endColumn, ct).ConfigureAwait(false);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<CodeActionInfo> Sort(IReadOnlyList<CodeActionInfo> items)
        => items
            .OrderBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.Title, StringComparer.Ordinal)
            .ToList();
}
