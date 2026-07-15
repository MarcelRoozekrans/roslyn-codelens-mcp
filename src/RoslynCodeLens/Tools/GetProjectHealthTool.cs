using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetProjectHealthTool
{
    [McpServerTool(Name = "get_project_health")]
    [Description(
        "Aggregate 7 health dimensions per project in one call: complexity hotspots, " +
        "large classes, naming violations, unused symbols, reflection usage, async " +
        "violations, and disposable misuse. Returns counts per dimension plus the top-N " +
        "hotspots inline (default 5) so the caller can prioritise without follow-up calls. " +
        "Use this when answering 'how is this project doing?' / 'where should I focus?' / " +
        "'what's the technical debt picture?'. " +
        "Underlying defaults: complexity threshold 10, large-class limits 20 members / 500 lines, " +
        "unused symbols excludes internals. " +
        "Test projects are skipped. Project filter is case-insensitive. Sort: projects ASC by " +
        "name; hotspots sorted by severity proxy per dimension (cyclomatic complexity desc, " +
        "line count desc, severity enum desc for async/disposable).")]
    public static GetProjectHealthResult Execute(
        MultiSolutionManager manager,
        [Description("Optional: restrict to a single project by name. Default: whole solution, grouped per project.")]
        string? project = null,
        [Description("How many hotspots to include per dimension. Default: 5. Pass 0 for counts-only output.")]
        int hotspotsPerDimension = 5)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return GetProjectHealthLogic.Execute(
            context.Loaded,
            context.Resolver,
            project,
            hotspotsPerDimension);
    }
}
