using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindObsoleteUsageTool
{
    [McpServerTool(Name = "find_obsolete_usage")]
    [Description(
        "Find every call site referencing [Obsolete]-marked symbols in the solution, " +
        "grouped by deprecation message and severity. Sharper than find_attribute_usages " +
        "for migration-planning workflows: tells you 'we have 5 distinct deprecations " +
        "pending; this one has 80 sites and is an error; that one has 3 sites and is a " +
        "warning.' " +
        "Includes both source-marked and metadata-marked obsoletes (third-party NuGet " +
        "deprecations are surfaced too). " +
        "Symbols with zero usages are omitted (no migration needed). " +
        "Sort: errors first, then by usage count descending, then by symbol name. " +
        "Test projects skipped. Project filter is case-insensitive.")]
    public static FindObsoleteUsageResult Execute(
        MultiSolutionManager manager,
        [Description("Optional: restrict to a single project by name (case-insensitive).")]
        string? project = null,
        [Description("If true, only [Obsolete(..., true)] error-level deprecations are returned. Default false.")]
        bool errorOnly = false)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return FindObsoleteUsageLogic.Execute(
            context.Loaded,
            context.Resolver,
            project,
            errorOnly);
    }
}
