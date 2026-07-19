using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ResolveStackTraceTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "resolve_stack_trace"),
     Description("Map a pasted .NET stack trace to file/line/symbol against the loaded solution, " +
                 "undoing compiler name mangling: async/iterator state machines (`<M>d__N.MoveNext`), " +
                 "lambdas (`<>c` / `<>c__DisplayClass`), local functions (`g__Name|`), generic arity. " +
                 "Handles Exception.ToString() output, log-prefixed lines, inner-exception chains, and " +
                 "Ben.Demystifier-style traces. Frames without 'in file:line' get the declaration site; " +
                 "frames with it keep the exact location. External frames resolve with origin=metadata. " +
                 "Items are in original trace order.")]
    public static ToolListResult<StackFrameInfo> Execute(
        MultiSolutionManager manager,
        [Description("The stack trace text, pasted as-is (multi-line)")] string stackTrace,
        [Description("Maximum number of items to return (default: 500)")] int? limit = null)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        var result = ResolveStackTraceLogic.Execute(
            context.Loaded, context.Resolver, context.Metadata, stackTrace);
        var frames = result.Frames;

        var summary = new
        {
            byOrigin = new
            {
                source = frames.Count(f => string.Equals(f.Origin, "source", StringComparison.Ordinal)),
                metadata = frames.Count(f => string.Equals(f.Origin, "metadata", StringComparison.Ordinal)),
                unresolved = frames.Count(f => string.Equals(f.Origin, "unresolved", StringComparison.Ordinal)),
            },
            exceptions = frames.Count(f => string.Equals(f.Kind, "exception", StringComparison.Ordinal)),
            skippedFrameLike = result.SkippedFrameLike,
        };
        return ToolListResult.Create(frames, limit ?? DefaultLimit, summary);
    }
}
