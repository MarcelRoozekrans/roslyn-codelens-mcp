using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class AnalyzeChangeImpactTool
{
    [McpServerTool(Name = "analyze_change_impact"),
     Description("Analyze the blast radius of changing a symbol — shows every file, project, and call site affected. " +
                 "Combines find_references and find_callers into a single impact summary. " +
                 "Use before renaming, changing signatures, or removing a type/method.")]
    public static ChangeImpact? Execute(
        MultiSolutionManager manager,
        [Description("Symbol name to analyze (type name, 'Type.Method', etc.)")] string symbol)
    {
        manager.EnsureLoaded();
        var loaded = manager.GetLoadedSolution();
        var resolver = manager.GetResolver();
        return AnalyzeChangeImpactLogic.Execute(loaded, resolver, symbol);
    }
}
