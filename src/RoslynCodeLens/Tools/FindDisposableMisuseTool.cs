using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindDisposableMisuseTool
{
    [McpServerTool(Name = "find_disposable_misuse")]
    [Description(
        "Detect IDisposable / IAsyncDisposable instances at risk of leaking. " +
        "Two patterns: local variables holding a disposable that aren't wrapped " +
        "in using/await using/returned/assigned-to-field-or-out-parameter (warning), " +
        "and bare-expression-statement discards of a disposable creator/factory (error). " +
        "Returns a summary plus a per-violation list (severity error/warning, location, " +
        "containing method, snippet). Skips test projects and generated code. " +
        "Scope: methods only (not constructors/accessors/operators); ownership transfer " +
        "via method/constructor argument is not detected. " +
        "Static analysis only — no fix suggestions.")]
    public static FindDisposableMisuseResult Execute(MultiSolutionManager manager)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return FindDisposableMisuseLogic.Execute(
            context.Loaded,
            context.Resolver);
    }
}
