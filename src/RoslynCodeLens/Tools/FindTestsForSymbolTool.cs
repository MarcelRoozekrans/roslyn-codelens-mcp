using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindTestsForSymbolTool
{
    [McpServerTool(Name = "find_tests_for_symbol"),
     Description("List test methods that exercise the given production symbol. Recognises xUnit, NUnit, and MSTest. Set transitive=true to follow helper methods up to maxDepth levels (default 3, max 5).")]
    public static FindTestsForSymbolResult Execute(
        MultiSolutionManager manager,
        [Description("Symbol name as Type.Method (simple or fully qualified)")] string symbol,
        [Description("Walk through helper methods to find indirect tests. Default false.")] bool transitive = false,
        [Description("Maximum walk depth when transitive=true. Clamped to [1, 5]. Default 3.")] int maxDepth = 3)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return FindTestsForSymbolLogic.Execute(
            context.Loaded,
            context.Resolver,
            symbol,
            transitive,
            maxDepth);
    }
}
