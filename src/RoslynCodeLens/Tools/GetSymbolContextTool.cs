using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetSymbolContextTool
{
    [McpServerTool(Name = "get_symbol_context"),
     Description("One-shot context dump for a type: namespace, base class, interfaces, injected dependencies, public members")]
    public static SymbolContext Execute(
        MultiSolutionManager manager,
        [Description("Type name (simple or fully qualified)")] string symbol,
        [Description(SolutionArgument.Description)] string? solution = null)
    {
        var sm = manager.Resolve(solution);
        sm.EnsureLoaded();
        var context = sm.GetAnalysisContext();
        return GetSymbolContextLogic.Execute(
            context.Loaded,
            context.Resolver,
            context.Metadata,
            symbol);
    }
}
