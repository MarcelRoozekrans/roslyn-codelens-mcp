using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetGeneratedCodeTool
{
    private const int DefaultLimit = 200;

    [McpServerTool(Name = "get_generated_code"),
     Description("Inspect generated source code from source generators. " +
                 "Returns an envelope with items sorted by project then file path, totalCount, truncated, and limit (default 200).")]
    public static ToolListResult<GeneratedFileInfo> Execute(
        MultiSolutionManager manager,
        [Description("Generator name to filter by")] string? generator = null,
        [Description("File path (or partial match) to filter by")] string? file = null,
        [Description("Maximum number of items to return (default: 200). Items are sorted by project, then file path.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = GetGeneratedCodeLogic.Execute(
            manager.GetLoadedSolution(), manager.GetResolver(), generator, file);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<GeneratedFileInfo> Sort(IReadOnlyList<GeneratedFileInfo> items)
        => items
            .OrderBy(g => g.Project, StringComparer.Ordinal)
            .ThenBy(g => g.FilePath, StringComparer.Ordinal)
            .ToList();
}
