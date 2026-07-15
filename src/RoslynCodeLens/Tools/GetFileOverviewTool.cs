using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetFileOverviewTool
{
    [McpServerTool(Name = "get_file_overview"),
     Description("Get a summary of a C# file: which types are defined in it and any compiler diagnostics. " +
                 "Useful for quickly understanding a file's contents without reading it.")]
    public static async Task<FileOverview> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the C# source file")] string filePath,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return await GetFileOverviewLogic.ExecuteAsync(context.Loaded, context.Resolver, filePath, ct).ConfigureAwait(false);
    }
}
