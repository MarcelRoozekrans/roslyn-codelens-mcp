using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetFileOverviewLogic
{
    public static async Task<FileOverview> ExecuteAsync(
        LoadedSolution loaded, SymbolResolver resolver, string filePath, CancellationToken ct)
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

        if (targetDocument == null || targetProject == null)
            throw new McpToolException(ToolErrorCode.FileNotFound, $"File '{filePath}' not found in any loaded project.", new { filePath });

        // Types defined in this file
        var syntaxTree = await targetDocument.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        var typesDefined = new List<string>();
        if (syntaxTree != null)
        {
            var root = await syntaxTree.GetRootAsync(ct).ConfigureAwait(false);
            typesDefined = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Select(t => t.Identifier.Text)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        // Diagnostics scoped to this file
        var projectName = resolver.GetProjectName(targetProject.Id);
        var diagnostics = GetDiagnosticsLogic.Execute(loaded, resolver, project: null, severity: null)
            .Where(d => d.File.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new FileOverview(normalizedPath, projectName, typesDefined, diagnostics);
    }
}
