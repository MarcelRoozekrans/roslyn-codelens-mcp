using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetCodeActionsLogic
{
    public static async Task<IReadOnlyList<CodeActionInfo>> ExecuteAsync(
        LoadedSolution loaded, string filePath, int line, int column,
        int? endLine, int? endColumn, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        Project? targetProject = null;
        Document? targetDocument = null;

        foreach (var project in loaded.Solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
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

        return await CodeActionRunner.GetActionsAsync(
            targetProject, targetDocument, compilation,
            line, column, endLine, endColumn, ct).ConfigureAwait(false);
    }
}
