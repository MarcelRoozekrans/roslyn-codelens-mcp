using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class AnalyzeDataFlowTool
{
    [McpServerTool(Name = "analyze_data_flow"),
     Description("Analyze data flow within a range of statements in a C# method. " +
                 "Returns variables declared, read, written, captured by lambdas, and flowing in/out of the region. " +
                 "Useful for understanding variable lifecycle before extracting code.")]
    public static async Task<DataFlowInfo?> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file")] string filePath,
        [Description("First line of the statement range (1-based)")] int startLine,
        [Description("Last line of the statement range (1-based)")] int endLine,
        [Description(SolutionArgument.Description)] string? solution = null,
        CancellationToken ct = default)
    {
        var sm = manager.Resolve(solution);
        sm.EnsureLoaded();
        return await AnalyzeDataFlowLogic.ExecuteAsync(
            sm.GetLoadedSolution(), filePath, startLine, endLine, ct).ConfigureAwait(false);
    }
}
