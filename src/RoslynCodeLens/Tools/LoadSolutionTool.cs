using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class LoadSolutionTool
{
    [McpServerTool(Name = "load_solution"),
     Description("Load a .sln/.slnx solution at runtime and make it the active solution. " +
                 "If the solution is already loaded, it simply activates it (~instant). " +
                 "New solutions take ~3 seconds to load and compile. " +
                 "Use this to dynamically switch between codebases without restarting the server.")]
    public static async Task<string> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the .sln or .slnx file to load")] string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Solution file not found: {path}");

        var normalised = await manager.LoadSolutionAsync(path).ConfigureAwait(false);
        var skipped = manager.GetActiveSkippedProjects();
        if (skipped.Count == 0)
            return $"Loaded and activated: {normalised}";

        var summary = string.Join(", ", skipped.Select(s => $"{s.Name} ({s.Kind})"));
        return $"Loaded and activated: {normalised}. Skipped {skipped.Count} project(s): {summary}. " +
               $"Call list_solutions for per-project reason details.";
    }
}
