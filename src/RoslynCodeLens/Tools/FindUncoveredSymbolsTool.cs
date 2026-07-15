using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindUncoveredSymbolsTool
{
    [McpServerTool(Name = "find_uncovered_symbols")]
    [Description(
        "Report public methods and properties that no test method transitively reaches " +
        "(within 3 helper hops). Output sorted by cyclomatic complexity descending, " +
        "with a coverage summary including a riskHotspotCount (uncovered with " +
        "complexity >= 5). Recognises xUnit, NUnit, MSTest. Reference-based static " +
        "analysis — does not parse runtime coverage data.")]
    public static FindUncoveredSymbolsResult Execute(MultiSolutionManager manager)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return FindUncoveredSymbolsLogic.Execute(
            context.Loaded,
            context.Resolver);
    }
}
