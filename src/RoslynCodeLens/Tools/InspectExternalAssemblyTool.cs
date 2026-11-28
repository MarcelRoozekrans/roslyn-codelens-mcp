using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class InspectExternalAssemblyTool
{
    [McpServerTool(Name = "inspect_external_assembly"),
     Description("Inspect a referenced closed-source assembly. mode='summary' returns namespaces + type counts; mode='namespace' returns public types and members for the given namespace.")]
    public static ExternalAssemblyOverview Execute(
        MultiSolutionManager manager,
        [Description("Assembly name, e.g. 'Newtonsoft.Json' or 'Microsoft.Extensions.DependencyInjection.Abstractions'")] string assemblyName,
        [Description("'summary' (default) or 'namespace'")] string mode = "summary",
        [Description("Required when mode='namespace'")] string? namespaceFilter = null,
        [Description(SolutionArgument.Description)] string? solution = null)
    {
        var sm = manager.Resolve(solution);
        sm.EnsureLoaded();
        return InspectExternalAssemblyLogic.Execute(
            sm.GetMetadataResolver(), assemblyName, mode, namespaceFilter);
    }
}
