using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetGeneratedCodeTool
{
    [McpServerTool(Name = "get_generated_code"),
     Description("Inspect generated source code from source generators")]
    public static IReadOnlyList<GeneratedFileInfo> Execute(
        MultiSolutionManager manager,
        [Description("Generator name to filter by")] string? generator = null,
        [Description("File path (or partial match) to filter by")] string? file = null)
    {
        manager.EnsureLoaded();
        return GetGeneratedCodeLogic.Execute(
            manager.GetLoadedSolution(), manager.GetResolver(), generator, file);
    }
}
