using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;

namespace RoslynCodeLens;

public static class CodeFixRunner
{
    public static async Task<IReadOnlyList<CodeFixSuggestion>> GetFixesAsync(
        Project project, Diagnostic diagnostic, CancellationToken ct)
    {
        var providers = GetCodeFixProviders(project, diagnostic.Id);
        if (providers.Count == 0)
            return [];

        var document = FindDocument(project, diagnostic.Location);
        if (document == null)
            return [];

        var suggestions = new List<CodeFixSuggestion>();

        for (var i = 0; i < providers.Count; i++)
        {
            var provider = providers[i];
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), ct);

            try
            {
                await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[roslyn-codelens] CodeFix registration failed ({provider.GetType().Name}): {ex}").ConfigureAwait(false);
                continue;
            }

            for (var j = 0; j < actions.Count; j++)
            {
                var action = actions[j];
                var edits = await ExtractTextEdits(action, project, ct).ConfigureAwait(false);
                if (edits.Count > 0)
                {
                    suggestions.Add(new CodeFixSuggestion(action.Title, diagnostic.Id, edits));
                }
            }
        }

        return suggestions;
    }

    private static async Task<List<TextEdit>> ExtractTextEdits(
        CodeAction action, Project project, CancellationToken ct)
    {
        var operations = await action.GetOperationsAsync(ct).ConfigureAwait(false);
        var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (applyOp == null) return [];

        var changedSolution = applyOp.ChangedSolution;
        var edits = new List<TextEdit>();

        foreach (var projectChange in changedSolution.GetChanges(project.Solution).GetProjectChanges())
        {
            foreach (var changedDocId in projectChange.GetChangedDocuments())
            {
                var originalDoc = project.Solution.GetDocument(changedDocId);
                var changedDoc = changedSolution.GetDocument(changedDocId);
                if (originalDoc == null || changedDoc == null) continue;

                var originalText = await originalDoc.GetTextAsync(ct).ConfigureAwait(false);
                var changedText = await changedDoc.GetTextAsync(ct).ConfigureAwait(false);
                var changes = changedText.GetTextChanges(originalText);

                foreach (var change in changes)
                {
                    var startLine = originalText.Lines.GetLinePosition(change.Span.Start);
                    var endLine = originalText.Lines.GetLinePosition(change.Span.End);

                    edits.Add(new TextEdit(
                        originalDoc.FilePath ?? "",
                        startLine.Line + 1, startLine.Character + 1,
                        endLine.Line + 1, endLine.Character + 1,
                        change.NewText ?? ""));
                }
            }
        }

        return edits;
    }

    private static List<CodeFixProvider> GetCodeFixProviders(Project project, string diagnosticId)
    {
        var analyzerProviders = project.AnalyzerReferences
            .Select(analyzerRef => LoadProvidersFromAnalyzer(analyzerRef, project.Language, diagnosticId))
            .SelectMany(p => p);

        return analyzerProviders.ToList();
    }

    private static IEnumerable<CodeFixProvider> LoadProvidersFromAnalyzer(
        AnalyzerReference analyzerRef, string projectLanguage, string diagnosticId)
    {
        var fullPath = analyzerRef.FullPath;
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            yield break;

        var types = LoadTypesFromAssembly(fullPath);
        if (types == null)
            yield break;

        foreach (var provider in FindMatchingProviders(types, projectLanguage, diagnosticId))
            yield return provider;
    }

    private static Type[]? LoadTypesFromAssembly(string fullPath)
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(fullPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[roslyn-codelens] Failed to load analyzer assembly '{fullPath}': {ex}");
            return null;
        }

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).ToArray()!;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[roslyn-codelens] Failed to get types from '{fullPath}': {ex}");
            return null;
        }
    }

    private static IEnumerable<CodeFixProvider> FindMatchingProviders(
        Type[] types, string projectLanguage, string diagnosticId)
    {
        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
                continue;

            var exportAttr = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
            if (exportAttr == null)
                continue;

            if (exportAttr.Languages != null && exportAttr.Languages.Length > 0 &&
                !exportAttr.Languages.Contains(projectLanguage, StringComparer.OrdinalIgnoreCase))
                continue;

            CodeFixProvider instance;
            try
            {
                instance = (CodeFixProvider)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[roslyn-codelens] Failed to create CodeFix provider '{type.Name}': {ex}");
                continue;
            }

            if (instance.FixableDiagnosticIds.Contains(diagnosticId, StringComparer.OrdinalIgnoreCase))
                yield return instance;
        }
    }

    private static Document? FindDocument(Project project, Location location)
    {
        if (!location.IsInSource || location.SourceTree == null)
            return null;

        return project.Documents.FirstOrDefault(d =>
            d.FilePath != null &&
            d.FilePath.Equals(location.SourceTree.FilePath, StringComparison.OrdinalIgnoreCase));
    }
}
