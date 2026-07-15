using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetCallGraphTool
{
    [McpServerTool(Name = "get_call_graph")]
    [Description(
        "Transitive caller and/or callee graph for a method symbol, depth-bounded with cycle " +
        "detection. Output is an adjacency-list dict per direction (callees and/or callers), " +
        "where each visited symbol maps to its outgoing edges. " +
        "Direction: 'callees' (what this method transitively calls), 'callers' (who reaches " +
        "this method), or 'both'. " +
        "External (BCL/NuGet) callees appear as terminal leaves with isExternal=true. " +
        "Declared signature only on callee side — no virtual dispatch resolution; agent uses " +
        "find_implementations separately if needed. Callers side resolves dispatch naturally " +
        "via Roslyn SymbolFinder. " +
        "Hard cap on total visited nodes (default 500) — sets truncated=true if hit; edges to " +
        "truncated targets are still recorded against the source node. " +
        "Use this instead of recursive find_callers / analyze_method calls when you need depth > 1.")]
    public static Task<GetCallGraphResult?> ExecuteAsync(
        MultiSolutionManager manager,
        [Description("Method symbol (e.g. 'Greeter.Greet' or 'MyNamespace.MyClass.MyMethod')")]
        string symbol,
        [Description("'callees' (default), 'callers', or 'both'.")]
        string direction = "callees",
        [Description("Max traversal depth from root. Default 3.")]
        int maxDepth = 3,
        [Description("Hard cap on total visited nodes. Default 500.")]
        int maxNodes = 500,
        CancellationToken cancellationToken = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return GetCallGraphLogic.ExecuteAsync(
            context.Loaded,
            context.Resolver,
            context.Metadata,
            symbol,
            direction,
            maxDepth,
            maxNodes,
            cancellationToken);
    }
}
