using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindBreakingChangesTool
{
    [McpServerTool(Name = "find_breaking_changes")]
    [Description(
        "Diff the current solution's public API surface against a baseline (JSON snapshot " +
        "from a prior get_public_api_surface run, or a baseline .dll file). Reports five " +
        "change kinds: Removed/KindChanged/AccessibilityNarrowed (Breaking) plus " +
        "Added/AccessibilityWidened (NonBreaking). Returns a summary plus a per-change list " +
        "(kind, severity, fully-qualified name, entity kind, project, file, line, details). " +
        "Sort: Breaking before NonBreaking, then name ASC. " +
        "Limitations: return type changes, sealed-ness changes, and nullable annotation " +
        "changes are not detected (PublicApiEntry schema doesn't capture them).")]
    public static FindBreakingChangesResult Execute(
        MultiSolutionManager manager,
        [Description("Path to a baseline .json snapshot (from a prior get_public_api_surface call) or a baseline .dll file.")]
        string baselinePath)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return FindBreakingChangesLogic.Execute(
            context.Loaded,
            context.Resolver,
            baselinePath);
    }
}
