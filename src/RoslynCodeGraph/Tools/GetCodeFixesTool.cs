using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetCodeFixesLogic
{
    public static async Task<List<CodeFixSuggestion>> ExecuteAsync(
        LoadedSolution loaded, SymbolResolver resolver,
        string diagnosticId, string filePath, int line,
        CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        Project? targetProject = null;
        Document? targetDocument = null;

        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null &&
                    doc.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    targetProject = project;
                    targetDocument = doc;
                    break;
                }
            }
            if (targetProject != null) break;
        }

        if (targetProject == null || targetDocument == null)
            return [];

        if (!loaded.Compilations.TryGetValue(targetProject.Id, out var compilation))
            return [];

        var analyzerRunner = new AnalyzerRunner();
        var allDiagnostics = compilation.GetDiagnostics()
            .Concat(await analyzerRunner.RunAnalyzersAsync(targetProject, compilation, ct));

        var matchingDiagnostics = allDiagnostics
            .Where(d => d.Id == diagnosticId &&
                        d.Location.IsInSource &&
                        d.Location.SourceTree?.FilePath != null &&
                        d.Location.SourceTree.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                        d.Location.GetLineSpan().StartLinePosition.Line + 1 == line)
            .ToList();

        if (matchingDiagnostics.Count == 0)
            return [];

        var fixRunner = new CodeFixRunner();
        var results = new List<CodeFixSuggestion>();

        foreach (var diagnostic in matchingDiagnostics)
        {
            var fixes = await fixRunner.GetFixesAsync(targetProject, diagnostic, ct);
            results.AddRange(fixes);
        }

        return results;
    }
}

[McpServerToolType]
public static class GetCodeFixesTool
{
    [McpServerTool(Name = "get_code_fixes"),
     Description("Get available code fixes for a specific diagnostic at a file location. Returns structured text edits that can be reviewed and applied.")]
    public static async Task<List<CodeFixSuggestion>> Execute(
        SolutionManager manager,
        [Description("Diagnostic ID (e.g., 'CA1822', 'CS0168')")] string diagnosticId,
        [Description("Full path to the source file")] string filePath,
        [Description("Line number where the diagnostic occurs")] int line,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await GetCodeFixesLogic.ExecuteAsync(manager.GetLoadedSolution(), manager.GetResolver(), diagnosticId, filePath, line, ct);
    }
}
