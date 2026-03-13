using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindAttributeUsagesTool
{
    [McpServerTool(Name = "find_attribute_usages"),
     Description("Find all types and members decorated with a specific attribute (e.g., Obsolete, Authorize, Serializable)")]
    public static IReadOnlyList<AttributeUsageInfo> Execute(
        MultiSolutionManager manager,
        [Description("Attribute name to search for (with or without 'Attribute' suffix)")] string attribute)
    {
        manager.EnsureLoaded();
        return FindAttributeUsagesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), attribute);
    }
}
