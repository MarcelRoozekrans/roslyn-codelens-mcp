using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindNamingViolationsTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_naming_violations"),
     Description("Check .NET naming convention compliance: PascalCase types/methods/properties, camelCase parameters, I-prefix interfaces, _ prefix private fields. " +
                 "Returns an envelope with items, totalCount, truncated, limit (default 500), and a byRule summary.")]
    public static ToolListResult<NamingViolation> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Maximum number of items to return (default: 500). Items are sorted by rule, then file.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindNamingViolationsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project);

        var sorted = Sort(raw);
        var summary = BuildSummary(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<NamingViolation> Sort(IReadOnlyList<NamingViolation> items)
        => items
            .OrderBy(n => n.Rule, StringComparer.Ordinal)
            .ThenBy(n => n.File, StringComparer.Ordinal)
            .ThenBy(n => n.Line)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<NamingViolation> items)
    {
        var byRule = items
            .GroupBy(n => n.Rule, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { byRule };
    }
}
