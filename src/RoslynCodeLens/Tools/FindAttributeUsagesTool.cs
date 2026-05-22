using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindAttributeUsagesTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_attribute_usages"),
     Description("Find all types and members decorated with a specific attribute (e.g., Obsolete, Authorize, Serializable). " +
                 "Returns an envelope with items, totalCount, truncated, limit, and a byProject summary.")]
    public static ToolListResult<AttributeUsageInfo> Execute(
        MultiSolutionManager manager,
        [Description("Attribute name to search for (with or without 'Attribute' suffix)")] string attribute,
        [Description("Maximum number of items to return (default: 500). Items are sorted by file, then line.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindAttributeUsagesLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            manager.GetMetadataResolver(),
            attribute);

        var sorted = Sort(raw);
        var summary = BuildSummary(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<AttributeUsageInfo> Sort(IReadOnlyList<AttributeUsageInfo> items)
        => items
            .OrderBy(a => a.File, StringComparer.Ordinal)
            .ThenBy(a => a.Line)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<AttributeUsageInfo> items)
    {
        var byProject = items
            .GroupBy(a => a.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { byProject };
    }
}
