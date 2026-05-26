using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class ApplyCodeActionLogic
{
    public static async Task<CodeActionResult> ExecuteAsync(
        LoadedSolution loaded, string filePath, int line, int column,
        int? endLine, int? endColumn,
        string actionTitle, bool preview, CancellationToken ct)
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
        {
            return new CodeActionResult(
                Success: false,
                Title: actionTitle,
                Edits: [],
                ErrorMessage: $"File not found in solution: {filePath}");
        }

        if (!loaded.Compilations.TryGetValue(targetProject.Id, out var compilation))
        {
            return new CodeActionResult(
                Success: false,
                Title: actionTitle,
                Edits: [],
                ErrorMessage: $"No compilation available for project: {targetProject.Name}");
        }

        return await CodeActionRunner.ApplyActionAsync(
            targetProject, targetDocument, compilation,
            line, column, endLine, endColumn,
            actionTitle, preview, ct).ConfigureAwait(false);
    }
}
