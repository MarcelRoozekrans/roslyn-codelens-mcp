using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetTypeOverviewTool
{
    [McpServerTool(Name = "get_type_overview"),
     Description("Get a comprehensive overview of a type in one call: full context (namespace, base class, interfaces, " +
                 "members, DI dependencies), type hierarchy (bases, interfaces, derived types), and diagnostics in the file. " +
                 "More efficient than calling get_symbol_context + get_type_hierarchy + get_diagnostics separately.")]
    public static TypeOverview Execute(
        MultiSolutionManager manager,
        [Description("Type name (simple name or fully qualified)")] string typeName)
    {
        manager.EnsureLoaded();
        var loaded = manager.GetLoadedSolution();
        var resolver = manager.GetResolver();
        var metadata = manager.GetMetadataResolver();
        return GetTypeOverviewLogic.Execute(loaded, resolver, metadata, typeName);
    }
}
