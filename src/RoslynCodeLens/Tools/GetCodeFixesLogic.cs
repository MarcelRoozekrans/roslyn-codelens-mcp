using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetCodeFixesLogic
{
    public static async Task<IReadOnlyList<CodeFixSuggestion>> ExecuteAsync(
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

        var allDiagnostics = compilation.GetDiagnostics()
            .Concat(await AnalyzerRunner.RunAnalyzersAsync(targetProject, compilation, ct).ConfigureAwait(false));

        var matchingDiagnostics = allDiagnostics
            .Where(d => string.Equals(d.Id, diagnosticId, StringComparison.Ordinal) &&
                        d.Location.IsInSource &&
                        d.Location.SourceTree?.FilePath != null &&
                        d.Location.SourceTree.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                        d.Location.GetLineSpan().StartLinePosition.Line + 1 == line)
            .ToList();

        if (matchingDiagnostics.Count == 0)
            return [];


        var results = new List<CodeFixSuggestion>();

        for (var i = 0; i < matchingDiagnostics.Count; i++)
        {
            var diagnostic = matchingDiagnostics[i];
            var fixes = await CodeFixRunner.GetFixesAsync(targetProject, diagnostic, ct).ConfigureAwait(false);
            results.AddRange(fixes);
        }

        return results;
    }
}
