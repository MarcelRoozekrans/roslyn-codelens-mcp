using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetExceptionFlowTool
{
    [McpServerTool(Name = "get_exception_flow")]
    [Description(
        "Which exceptions can escape a method — the 'is calling this safe?' question. Walks the " +
        "method's callees depth-first (cycle-safe, depth- and node-bounded) collecting every " +
        "explicit throw, then propagates each one back up the call chain, testing the enclosing " +
        "`try`/`catch` at the throw and at every call site on the way out. " +
        "Each item reports `escapes`, the `path` of methods from the analysed method to the one " +
        "that raises it, and — when it is stopped — `caughtIn`, `caughtFile`, `caughtLine`. " +
        "A handler with a `when` filter sets `hasFilter` and the exception is still reported as " +
        "escaping, because the filter may decline at runtime. " +
        "`origin` is `thrown` for a real throw site in source, or `documented` for an " +
        "`exception` XML doc tag on a metadata (BCL/NuGet) callee — set `includeDocumented` to " +
        "false to drop the latter. Throws inside lambdas and local functions are excluded: they " +
        "escape when that body runs, not at this method's boundary — use `find_throw_sites` for " +
        "a plain textual scan of throws. " +
        "Static analysis only: no reflection, no virtual-dispatch resolution, and no implicit " +
        "runtime failures such as null dereferences. Hitting `maxDepth` or `maxNodes` sets " +
        "`truncated`.")]
    public static ExceptionFlowResult Execute(
        MultiSolutionManager manager,
        [Description("Method symbol to analyse (e.g. `OrderService.Place` or `MyNamespace.MyClass.MyMethod`).")]
        string method,
        [Description("Max callee depth to walk from the analysed method. Default 3.")]
        int maxDepth = 3,
        [Description("Include exceptions documented by `exception` XML tags on metadata callees. Default true.")]
        bool includeDocumented = true,
        [Description("Hard cap on methods visited. Default 500.")]
        int maxNodes = 500,
        CancellationToken cancellationToken = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();

        return GetExceptionFlowLogic.Execute(
            context.Loaded,
            context.Resolver,
            context.Metadata,
            method,
            maxDepth,
            includeDocumented,
            maxNodes,
            cancellationToken);
    }
}
