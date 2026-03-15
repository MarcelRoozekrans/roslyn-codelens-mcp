using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class AnalyzeMethodTool
{
    [McpServerTool(Name = "analyze_method"),
     Description("Get a comprehensive analysis of a method in one call: signature, location, all callers, " +
                 "and all outgoing calls (methods this method invokes). " +
                 "More efficient than calling find_callers separately.")]
    public static MethodAnalysis? Execute(
        MultiSolutionManager manager,
        [Description("Method symbol (e.g. 'Greeter.Greet' or 'MyNamespace.MyClass.MyMethod')")] string symbol)
    {
        manager.EnsureLoaded();
        var loaded = manager.GetLoadedSolution();
        var resolver = manager.GetResolver();
        return AnalyzeMethodLogic.Execute(loaded, resolver, symbol);
    }
}
