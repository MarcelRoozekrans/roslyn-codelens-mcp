using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class UnloadSolutionTool
{
    [McpServerTool(Name = "unload_solution"),
     Description("Unload a previously loaded solution to free memory. " +
                 "Use a partial, case-insensitive name (e.g. 'DcpCore' matches the full path). " +
                 "If the unloaded solution was active, another loaded solution becomes active. " +
                 "Use this when you're done analyzing a codebase and want to reclaim memory.")]
    public static string Execute(
        MultiSolutionManager manager,
        [Description("Solution to match: an absolute path matches exactly; otherwise an exact file name (e.g. `App.sln`) wins, then a case-insensitive substring of the path")] string name)
    {
        var unloaded = manager.UnloadSolution(name);
        return $"Unloaded: {unloaded}";
    }
}
