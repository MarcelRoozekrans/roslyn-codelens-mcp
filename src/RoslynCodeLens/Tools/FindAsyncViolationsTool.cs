using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindAsyncViolationsTool
{
    [McpServerTool(Name = "find_async_violations")]
    [Description(
        "Detect six classes of async/await misuse across all production projects: " +
        "sync-over-async (.Result, .Wait*, GetAwaiter().GetResult()), async void " +
        "outside event handlers, missing await in async methods, and fire-and-forget " +
        "tasks. Returns a summary plus a per-violation list (severity error/warning, " +
        "location, containing method, snippet). Skips test projects and generated " +
        "code. Static analysis only — no fix suggestions.")]
    public static FindAsyncViolationsResult Execute(MultiSolutionManager manager)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return FindAsyncViolationsLogic.Execute(
            context.Loaded,
            context.Resolver);
    }
}
