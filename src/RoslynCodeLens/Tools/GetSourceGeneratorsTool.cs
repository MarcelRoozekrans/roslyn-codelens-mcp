using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetSourceGeneratorsTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "get_source_generators"),
     Description("List source generators and their output per project. " +
                 "Returns an envelope with items sorted by project then generator name, totalCount, truncated, and limit (default 100).")]
    public static ToolListResult<SourceGeneratorInfo> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Maximum number of items to return (default: 100). Items are sorted by project, then generator name.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = GetSourceGeneratorsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<SourceGeneratorInfo> Sort(IReadOnlyList<SourceGeneratorInfo> items)
        => items
            .OrderBy(s => s.Project, StringComparer.Ordinal)
            .ThenBy(s => s.GeneratorName, StringComparer.Ordinal)
            .ToList();
}
