using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindEventSubscribersTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_event_subscribers")]
    [Description(
        "Find every += and -= site for an event symbol across the solution. " +
        "Accepts source events (e.g. 'MyClass.Clicked') or metadata events " +
        "(e.g. 'System.Diagnostics.Process.Exited'). " +
        "Each result reports the source location, the resolved handler (method FQN, " +
        "or a synthetic name like 'lambda at File.cs:N' for inline handlers), and the subscription kind " +
        "(Subscribe for +=, Unsubscribe for -=). " +
        "Use this for memory-leak audits (compare subscribe/unsubscribe pairs), " +
        "UI event subscriber inspection, or when Grep over '+= EventName' would miss " +
        "qualified or fully-typed subscription sites. " +
        "Returns an envelope with items sorted by file path then line, totalCount, truncated, and limit (default 500).")]
    public static ToolListResult<EventSubscriberInfo> Execute(
        MultiSolutionManager manager,
        [Description("Event symbol (e.g. 'MyClass.Clicked' or 'System.Diagnostics.Process.Exited')")]
        string symbol,
        [Description("Maximum number of items to return (default: 500). Items are sorted by file path, then line.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindEventSubscribersLogic.Execute(
            manager.GetLoadedSolution(),
            manager.GetResolver(),
            manager.GetMetadataResolver(),
            symbol);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<EventSubscriberInfo> Sort(IReadOnlyList<EventSubscriberInfo> items)
        => items
            .OrderBy(e => e.FilePath, StringComparer.Ordinal)
            .ThenBy(e => e.Line)
            .ToList();
}
