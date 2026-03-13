using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetDiRegistrationsTool
{
    [McpServerTool(Name = "get_di_registrations"),
     Description("Scan IServiceCollection extension methods for DI registrations of a type")]
    public static IReadOnlyList<DiRegistration> Execute(
        MultiSolutionManager manager,
        [Description("Type name to search for (simple or fully qualified)")] string symbol)
    {
        manager.EnsureLoaded();
        return GetDiRegistrationsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
    }
}
