using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class LoadSolutionTool
{
    [McpServerTool(Name = "load_solution"),
     Description("Load a .sln/.slnx solution at runtime and make it the active solution. " +
                 "Pass `include` (glob array against project name) or `rootProjects` (exact names) to " +
                 "load only a subset; the loader walks ProjectReference transitively from those seeds " +
                 "to keep the workspace semantically complete. If the same `path` is already loaded, " +
                 "providing a new filter disposes the previous workspace (replace semantics). " +
                 "If the solution is already loaded with no filter, it simply activates it (~instant). " +
                 "New solutions take ~3 seconds to load and compile.")]
    public static async Task<string> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the .sln or .slnx file to load")] string path,
        [Description("Optional glob patterns against project name (e.g. 'MyApp.*')")] string[]? include = null,
        [Description("Optional exact project names; both arrays act as seeds for a transitive ProjectReference closure")] string[]? rootProjects = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Solution file not found: {path}");

        ProjectFilter? filter = (include?.Length > 0 || rootProjects?.Length > 0)
            ? new ProjectFilter(include ?? Array.Empty<string>(), rootProjects ?? Array.Empty<string>())
            : null;

        var normalised = await manager.LoadSolutionAsync(path, filter).ConfigureAwait(false);
        var skipped = manager.GetActiveSkippedProjects();
        var loadedCount = manager.GetLoadedSolution().Solution.Projects.Count();

        if (skipped.Count == 0)
            return $"Loaded {loadedCount} project(s) from: {normalised}";

        var summary = string.Join(", ", skipped.Take(10).Select(s => $"{s.Name} ({s.Kind})"));
        var ellipsis = skipped.Count > 10 ? $" and {skipped.Count - 10} more" : "";
        return $"Loaded {loadedCount} project(s) from: {normalised}; skipped {skipped.Count}: {summary}{ellipsis}. " +
               "Call list_solutions for per-project reason details.";
    }
}
