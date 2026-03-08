using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynCodeGraph.Tools;

[McpServerToolType]
public static class RebuildSolutionTool
{
    [McpServerTool(Name = "rebuild_solution"),
     Description("Force a full reload of the analyzed solution — re-opens the .sln, recompiles all projects, and rebuilds all indexes. Use after changing Directory.Build.props, adding/removing NuGet packages, or when diagnostics seem stale.")]
    public static async Task<string> Execute(SolutionManager manager)
    {
        manager.EnsureLoaded();
        var (projectCount, elapsed) = await manager.ForceReloadAsync().ConfigureAwait(false);
        return $"Rebuild complete. {projectCount} project(s) compiled in {elapsed.TotalSeconds:F1}s.";
    }
}
