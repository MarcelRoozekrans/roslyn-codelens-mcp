using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class PeekIlTool
{
    [McpServerTool(Name = "peek_il"),
     Description("Return ilasm-style IL for a single method in a referenced closed-source assembly. Input must be a fully-qualified method name with parameter types.")]
    public static IlPeekResult Execute(
        MultiSolutionManager manager,
        [Description("Fully-qualified method name with parameter types, e.g. 'Newtonsoft.Json.JsonConvert.SerializeObject(object)'")] string methodSymbol)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return PeekIlLogic.Execute(
            context.Loaded,
            context.Metadata,
            manager.GetIlDisassembler(),
            methodSymbol);
    }
}
