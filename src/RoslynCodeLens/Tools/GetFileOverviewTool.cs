using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetFileOverviewTool
{
    [McpServerTool(Name = "get_file_overview"),
     Description("Get a summary of a C# file: which types are defined in it and any compiler diagnostics. " +
                 "Useful for quickly understanding a file's contents without reading it. " +
                 "Also accepts .razor/.cshtml markup, resolving it to the C# document its source generator produced.")]
    public static async Task<FileOverview> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the source file (.cs, or .razor/.cshtml)")] string filePath,
        [Description(SolutionArgument.Description)] string? solution = null,
        CancellationToken ct = default)
    {
        var sm = manager.Resolve(solution);
        sm.EnsureLoaded();
        var context = sm.GetAnalysisContext();
        return await GetFileOverviewLogic.ExecuteAsync(context.Loaded, context.Resolver, filePath, ct).ConfigureAwait(false);
    }
}
