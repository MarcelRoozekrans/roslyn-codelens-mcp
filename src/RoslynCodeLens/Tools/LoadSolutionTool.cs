using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.BackgroundTasks;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class LoadSolutionTool
{
    [McpServerTool(Name = "load_solution"),
     Description("Load a .sln/.slnx solution at runtime and make it the active solution. " +
                 "Both `include` and `rootProjects` match against the project FILE NAME without " +
                 "extension (the .csproj/.vbproj base name), NOT the assembly name. " +
                 "Pass `include` (case-insensitive glob array, supporting only `*` and `?` " +
                 "wildcards — character classes like `[...]` are NOT supported) or `rootProjects` " +
                 "(EXACT, case-sensitive file names) to load only a subset; the loader walks ProjectReference " +
                 "transitively from those seeds to keep the workspace semantically complete. " +
                 "A filter that matches NO project is an ERROR (the call fails) — it does NOT " +
                 "silently fall back to a full load; if unsure of exact names, do a no-filter load " +
                 "first and call list_solutions to discover them. If the same `path` is already loaded, " +
                 "providing a new filter disposes the previous workspace (replace semantics). " +
                 "If the solution is already loaded with no filter, it simply activates it (~instant). " +
                 "New solutions take ~3 seconds to load and compile. " +
                 "For very large solutions (hundreds of projects) that take minutes to open, pass " +
                 "`background: true` to return a taskId immediately instead of blocking; poll it with " +
                 "get_task_status until it succeeds (its result carries the loaded/skipped counts). " +
                 "The new solution only becomes active once the background load finishes, so other " +
                 "tools keep working against the current solution meanwhile.")]
    public static async Task<object> Execute(
        MultiSolutionManager manager,
        BackgroundTaskStore store,
        [Description("Full path to the .sln or .slnx file to load")] string path,
        [Description("Optional case-insensitive glob patterns against project file name without extension (e.g. 'MyApp.*')")] string[]? include = null,
        [Description("Optional exact project file names without extension; both arrays act as seeds for a transitive ProjectReference closure")] string[]? rootProjects = null,
        [Description("If true, load on a background task and return a taskId immediately (poll with get_task_status) instead of blocking until the solution is ready. Default false.")] bool background = false)
    {
        // Validate up front in both modes so a bad path fails fast rather than
        // surfacing only when the caller polls the background task.
        if (!File.Exists(path))
            throw new FileNotFoundException($"Solution file not found: {path}");

        ProjectFilter? filter = (include?.Length > 0 || rootProjects?.Length > 0)
            ? new ProjectFilter(include ?? Array.Empty<string>(), rootProjects ?? Array.Empty<string>())
            : null;

        if (background)
        {
            return store.Start("load_solution", async _ =>
            {
                var loadedPath = await manager.LoadSolutionAsync(path, filter).ConfigureAwait(false);
                var skippedProjects = manager.GetActiveSkippedProjects();
                var projectCount = manager.GetLoadedSolution().Solution.Projects.Count();
                return new
                {
                    path = loadedPath,
                    loadedProjects = projectCount,
                    skippedProjects = skippedProjects.Count,
                    skipped = skippedProjects
                        .Take(10)
                        .Select(s => new { s.Name, s.Kind, s.Reason })
                        .ToArray(),
                };
            });
        }

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
