using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetCodeFixesTool
{
    [McpServerTool(Name = "get_code_fixes"),
     Description("Get available code fixes for a specific diagnostic at a file location. Returns structured text edits that can be reviewed and applied.")]
    public static async Task<IReadOnlyList<CodeFixSuggestion>> Execute(
        MultiSolutionManager manager,
        [Description("Diagnostic ID (e.g., 'CA1822', 'CS0168')")] string diagnosticId,
        [Description("Full path to the source file")] string filePath,
        [Description("Line number where the diagnostic occurs")] int line,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await GetCodeFixesLogic.ExecuteAsync(manager.GetLoadedSolution(), manager.GetResolver(), diagnosticId, filePath, line, ct).ConfigureAwait(false);
    }
}
