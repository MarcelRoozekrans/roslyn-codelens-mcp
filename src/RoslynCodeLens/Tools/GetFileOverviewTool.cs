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
        var loaded = manager.GetLoadedSolution();
        var resolver = manager.GetResolver();
        return await GetFileOverviewLogic.ExecuteAsync(loaded, resolver, filePath, ct).ConfigureAwait(false);
    }
}
