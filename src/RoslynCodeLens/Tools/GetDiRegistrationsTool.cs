using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetDiRegistrationsTool
{
    private const int DefaultLimit = 200;

    [McpServerTool(Name = "get_di_registrations"),
     Description("Scan IServiceCollection extension methods for DI registrations of a type. " +
                 "Returns an envelope with items sorted by service name, totalCount, truncated, and limit (default 200).")]
    public static ToolListResult<DiRegistration> Execute(
        MultiSolutionManager manager,
        [Description("Type name to search for (simple or fully qualified)")] string symbol,
        [Description("Maximum number of items to return (default: 200). Items are sorted by service name.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = GetDiRegistrationsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<DiRegistration> Sort(IReadOnlyList<DiRegistration> items)
        => items
            .OrderBy(d => d.Service, StringComparer.Ordinal)
            .ThenBy(d => d.File, StringComparer.Ordinal)
            .ThenBy(d => d.Line)
            .ToList();
}
