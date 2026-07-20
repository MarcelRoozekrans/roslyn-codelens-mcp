using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindThrowSitesTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_throw_sites")]
    [Description(
        "Find every place an exception type is thrown across the solution — `throw new T(...)`, " +
        "`throw expr;` and bare `throw;` rethrows (whose type comes from the enclosing `catch`). " +
        "The exception type may live in source or in metadata, so `System.ArgumentNullException` " +
        "works as well as your own `MyApp.DomainException`. " +
        "Set `includeDerived` to also match subclasses of the requested type. " +
        "Every throw in the file is reported, including throws inside lambdas and local " +
        "functions, attributed to the member that contains them — use `get_exception_flow` " +
        "instead when the question is which exceptions escape a specific method. " +
        "Returns an envelope with items, totalCount, truncated, limit and a `byType` / " +
        "`byProject` summary, sorted by file, line, column.")]
    public static ToolListResult<ThrowSiteInfo> Execute(
        MultiSolutionManager manager,
        [Description("Exception type to search for (e.g. `System.InvalidOperationException` or `MyApp.DomainException`).")]
        string exceptionType,
        [Description("Also match types deriving from the requested one. Default false (exact type only).")]
        bool includeDerived = false,
        [Description("Maximum number of items to return (default: 500).")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();

        var raw = FindThrowSitesLogic.Execute(
            context.Loaded,
            context.Resolver,
            context.Metadata,
            exceptionType,
            includeDerived,
            cancellationToken);

        return ToolListResult.Create(Sort(raw), limit ?? DefaultLimit, BuildSummary(raw));
    }

    internal static IReadOnlyList<ThrowSiteInfo> Sort(IReadOnlyList<ThrowSiteInfo> items)
        => items
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ThenBy(s => s.Column)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<ThrowSiteInfo> items)
    {
        var byType = items
            .GroupBy(s => s.ExceptionType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var byProject = items
            .GroupBy(s => s.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new { byType, byProject };
    }
}
