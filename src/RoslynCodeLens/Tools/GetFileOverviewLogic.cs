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

        var (targetProject, targetDocument) = FindDocument(loaded, normalizedPath);

        // Markup files (.razor/.cshtml) are AdditionalDocuments, not C# documents — the C# for
        // them only exists as source-generator output. Resolving through to that output is what
        // makes a component with no code-behind inspectable at all (issue #399).
        if (targetDocument == null)
            (targetProject, targetDocument) = await FindGeneratedDocumentForMarkupAsync(loaded, normalizedPath, ct).ConfigureAwait(false);

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

        // Diagnostics scoped to this file. When the request resolved through to generated output,
        // diagnostics can be attributed to either path: Razor emits #line directives, so the
        // compiler reports most of them against the .razor file, but anything outside a mapped
        // region still carries the generated path.
        var generatedPath = targetDocument.FilePath;
        var projectName = resolver.GetProjectName(targetProject.Id);
        var diagnostics = GetDiagnosticsLogic.Execute(loaded, resolver, project: null, severity: null)
            .Where(d => d.File.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)
                     || (generatedPath != null && d.File.Equals(generatedPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new FileOverview(normalizedPath, projectName, typesDefined, diagnostics);
    }

    private static (Project?, Document?) FindDocument(LoadedSolution loaded, string normalizedPath)
    {
        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null &&
                    doc.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return (project, doc);
                }
            }
        }
        return (null, null);
    }

    /// <summary>
    /// Maps a markup file to the source-generated document produced from it, in two tiers.
    ///
    /// First by hint-name convention: the Razor generator names its output after the component's
    /// project-relative path, so <c>Components/Pages/Counter.razor</c> becomes hint
    /// <c>Components/Pages/Counter_razor.g.cs</c>. That is exact when it matches.
    ///
    /// Then by line mappings, which any generator emitting <c>#line</c> directives provides, so the
    /// mapping survives a change in naming convention. A unique match is required: a shared file
    /// such as <c>_Imports.razor</c> line-maps into every component in the project, and picking an
    /// arbitrary one of those would be worse than reporting nothing.
    /// </summary>
    private static async Task<(Project?, Document?)> FindGeneratedDocumentForMarkupAsync(
        LoadedSolution loaded, string normalizedPath, CancellationToken ct)
    {
        foreach (var project in loaded.Solution.Projects)
        {
            var additional = project.AdditionalDocuments.FirstOrDefault(
                d => d.FilePath != null && d.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (additional == null)
                continue;

            var generated = (await project.GetSourceGeneratedDocumentsAsync(ct).ConfigureAwait(false)).ToList();

            var expectedHint = Analysis.MarkupDocumentMap.ExpectedHintName(additional);
            foreach (var candidate in generated)
            {
                if (NormalizeHint(candidate.HintName).Equals(NormalizeHint(expectedHint), StringComparison.OrdinalIgnoreCase))
                    return (project, candidate);
            }

            Document? unique = null;
            foreach (var candidate in generated)
            {
                if (!await MapsToSourceFileAsync(candidate, normalizedPath, ct).ConfigureAwait(false))
                    continue;
                if (unique != null)
                    return (null, null); // ambiguous — shared markup such as _Imports.razor
                unique = candidate;
            }

            return unique != null ? (project, unique) : (null, null);
        }

        return (null, null);
    }

    private static string NormalizeHint(string hintName) => hintName.Replace('\\', '/');

    private static async Task<bool> MapsToSourceFileAsync(Document generated, string normalizedPath, CancellationToken ct)
    {
        var tree = await generated.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (tree == null)
            return false;

        foreach (var mapping in tree.GetLineMappings(ct))
        {
            if (mapping.IsHidden || !mapping.MappedSpan.HasMappedPath)
                continue;
            if (mapping.MappedSpan.Path.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
