using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindNamingViolationsTool
{
    [McpServerTool(Name = "find_naming_violations"),
     Description("Check .NET naming convention compliance: PascalCase types/methods/properties, camelCase parameters, I-prefix interfaces, _ prefix private fields")]
    public static IReadOnlyList<NamingViolation> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null)
    {
        manager.EnsureLoaded();
        return FindNamingViolationsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project);
    }
}
