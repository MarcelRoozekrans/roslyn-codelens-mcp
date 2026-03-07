using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph;

public class CodeFixRunner
{
    public async Task<List<CodeFixSuggestion>> GetFixesAsync(
        Project project, Diagnostic diagnostic, CancellationToken ct)
    {
        var providers = GetCodeFixProviders(project, diagnostic.Id);
        if (providers.Count == 0)
            return [];

        var document = FindDocument(project, diagnostic.Location);
        if (document == null)
            return [];

        var suggestions = new List<CodeFixSuggestion>();

        foreach (var provider in providers)
        {
            var actions = new List<CodeAction>();
            var context = new CodeFixContext(document, diagnostic,
                (action, _) => actions.Add(action), ct);

            try
            {
                await provider.RegisterCodeFixesAsync(context);
            }
            catch
            {
                continue;
            }

            foreach (var action in actions)
            {
                var operations = await action.GetOperationsAsync(ct);
                var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
                if (applyOp == null) continue;

                var changedSolution = applyOp.ChangedSolution;
                var edits = new List<TextEdit>();

                foreach (var changedDocId in changedSolution.GetChanges(project.Solution).GetProjectChanges()
                    .SelectMany(pc => pc.GetChangedDocuments()))
                {
                    var originalDoc = project.Solution.GetDocument(changedDocId);
                    var changedDoc = changedSolution.GetDocument(changedDocId);
                    if (originalDoc == null || changedDoc == null) continue;

                    var originalText = await originalDoc.GetTextAsync(ct);
                    var changedText = await changedDoc.GetTextAsync(ct);
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

                if (edits.Count > 0)
                {
                    suggestions.Add(new CodeFixSuggestion(action.Title, diagnostic.Id, edits));
                }
            }
        }

        return suggestions;
    }

    private static List<CodeFixProvider> GetCodeFixProviders(Project project, string diagnosticId)
    {
        var providers = new List<CodeFixProvider>();

        foreach (var analyzerRef in project.AnalyzerReferences)
        {
            var fullPath = analyzerRef.FullPath;
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                continue;

            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(fullPath);
            }
            catch
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
                    continue;

                var exportAttr = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
                if (exportAttr == null)
                    continue;

                // Check language compatibility
                if (exportAttr.Languages != null && exportAttr.Languages.Length > 0 &&
                    !exportAttr.Languages.Contains(project.Language))
                    continue;

                CodeFixProvider instance;
                try
                {
                    instance = (CodeFixProvider)Activator.CreateInstance(type)!;
                }
                catch
                {
                    continue;
                }

                if (instance.FixableDiagnosticIds.Contains(diagnosticId))
                    providers.Add(instance);
            }
        }

        return providers;
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
