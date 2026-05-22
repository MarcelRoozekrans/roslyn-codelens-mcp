using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetCodeFixesTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "get_code_fixes"),
     Description("Get available code fixes for a specific diagnostic at a file location. Returns structured text edits that can be reviewed and applied. " +
                 "Returns an envelope with items sorted by title, totalCount, truncated, and limit (default 100).")]
    public static async Task<ToolListResult<CodeFixSuggestion>> Execute(
        MultiSolutionManager manager,
        Security.TrustStore trustStore,
        Security.AnalyzerAllowlist allowlist,
        [Description("Diagnostic ID (e.g., 'CA1822', 'CS0168')")] string diagnosticId,
        [Description("Full path to the source file")] string filePath,
        [Description("Line number where the diagnostic occurs")] int line,
        [Description("Maximum number of items to return (default: 100). Items are sorted by title.")]
            int? limit = null,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        var raw = await GetCodeFixesLogic.ExecuteAsync(manager.GetLoadedSolution(), manager.GetResolver(), diagnosticId, filePath, line, trustStore, allowlist, ct).ConfigureAwait(false);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<CodeFixSuggestion> Sort(IReadOnlyList<CodeFixSuggestion> items)
        => items
            .OrderBy(f => f.Title, StringComparer.Ordinal)
            .ToList();
}
