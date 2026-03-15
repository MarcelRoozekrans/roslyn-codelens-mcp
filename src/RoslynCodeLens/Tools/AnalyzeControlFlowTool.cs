using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class AnalyzeControlFlowTool
{
    [McpServerTool(Name = "analyze_control_flow"),
     Description("Analyze control flow within a range of statements in a C# method. " +
                 "Returns reachability of start/end points, return statements, and exit points. " +
                 "Useful for detecting unreachable code and understanding branching.")]
    public static async Task<ControlFlowInfo?> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file")] string filePath,
        [Description("First line of the statement range (1-based)")] int startLine,
        [Description("Last line of the statement range (1-based)")] int endLine,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await AnalyzeControlFlowLogic.ExecuteAsync(
            manager.GetLoadedSolution(), filePath, startLine, endLine, ct).ConfigureAwait(false);
    }
}
