using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindCatchBlocksTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_catch_blocks")]
    [Description(
        "Find the `catch` clauses that handle an exception type across the solution, and say " +
        "what each one does with it. Each item reports `hasFilter` (a `when` clause, so the " +
        "handler may decline at runtime), `rethrows` (the body contains a bare `throw;`) and " +
        "`isEmpty` (an empty handler body) — together these answer 'who is silently swallowing " +
        "this exception?' in a single call. " +
        "By default only clauses declaring exactly the requested type match; set " +
        "`includeBaseClauses` to also surface handlers that catch a base type, including " +
        "`catch (Exception)` and bare `catch`, since those bind the requested type too. " +
        "`caughtType` is null for a bare `catch`. The exception type may live in source or in " +
        "metadata. " +
        "Returns an envelope with items, totalCount, truncated, limit and a `byType` / " +
        "`byProject` summary, sorted by file, line, column.")]
    public static ToolListResult<CatchBlockInfo> Execute(
        MultiSolutionManager manager,
        [Description("Exception type to search for (e.g. `System.IO.IOException` or `MyApp.DomainException`).")]
        string exceptionType,
        [Description("Also match clauses catching a base type of the requested one, including bare `catch`. Default false.")]
        bool includeBaseClauses = false,
        [Description("Maximum number of items to return (default: 500).")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();

        var raw = FindCatchBlocksLogic.Execute(
            context.Loaded,
            context.Resolver,
            context.Metadata,
            exceptionType,
            includeBaseClauses,
            cancellationToken);

        return ToolListResult.Create(Sort(raw), limit ?? DefaultLimit, BuildSummary(raw));
    }

    internal static IReadOnlyList<CatchBlockInfo> Sort(IReadOnlyList<CatchBlockInfo> items)
        => items
            .OrderBy(c => c.File, StringComparer.Ordinal)
            .ThenBy(c => c.Line)
            .ThenBy(c => c.Column)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<CatchBlockInfo> items)
    {
        var byType = items
            .GroupBy(c => c.CaughtType ?? "(bare catch)", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var byProject = items
            .GroupBy(c => c.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var swallowing = items.Count(c => c.IsEmpty && !c.Rethrows);

        return new { byType, byProject, swallowing };
    }
}
