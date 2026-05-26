// EPC12: Reflection-based provider loading — intentionally catches all exceptions and reports ex.Message to stderr
// EPS06: ImmutableArray.Where — acceptable allocation in non-hot-path diagnostic filtering
// HLQ012: foreach on List<Assembly> — CollectionsMarshal.AsSpan not applicable (async context / conditional break)
// MA0051: CollectRawActionsAsync and provider-loader methods naturally exceed 60 lines; splitting would obscure the algorithm
#pragma warning disable EPC12, EPS06, HLQ012, MA0051
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;

namespace RoslynCodeLens;

public static class CodeActionRunner
{
    private static readonly Lazy<ImmutableArray<CodeRefactoringProvider>> s_refactoringProviders =
        new(LoadBuiltInRefactoringProviders);

    private static readonly Lazy<ImmutableArray<CodeFixProvider>> s_codeFixProviders =
        new(LoadBuiltInCodeFixProviders);

    public static async Task<IReadOnlyList<CodeActionInfo>> GetActionsAsync(
        Project project, Document document, Compilation compilation,
        int line, int column, int? endLine, int? endColumn, CancellationToken ct)
    {
        var (actions, codeFixActionTitles) = await CollectRawActionsAsync(
            project, document, compilation, line, column, endLine, endColumn, ct).ConfigureAwait(false);

        return actions.Select(a => ToCodeActionInfo(a, codeFixActionTitles)).ToList();
    }

    public static async Task<CodeActionResult> ApplyActionAsync(
        Project project, Document document, Compilation compilation,
        int line, int column, int? endLine, int? endColumn,
        string actionTitle, bool preview, CancellationToken ct)
    {
        try
        {
            var (actions, _) = await CollectRawActionsAsync(
                project, document, compilation, line, column, endLine, endColumn, ct).ConfigureAwait(false);

            var matchedAction = FindAction(actions, actionTitle);
            if (matchedAction == null)
            {
                return new CodeActionResult(false, actionTitle, [],
                    $"No action found matching title '{actionTitle}'");
            }

            var operations = await matchedAction.GetOperationsAsync(ct).ConfigureAwait(false);
            var applyOp = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
            if (applyOp == null)
            {
                return new CodeActionResult(false, matchedAction.Title, [],
                    "Action did not produce any applicable changes");
            }

            var edits = await ExtractTextEdits(applyOp.ChangedSolution, project.Solution, ct).ConfigureAwait(false);

            if (!preview)
            {
                await WriteChangesToDiskAsync(applyOp.ChangedSolution, project.Solution, ct).ConfigureAwait(false);
            }

            return new CodeActionResult(true, matchedAction.Title, edits);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CodeActionResult(false, actionTitle, [],
                $"Failed to apply action: {ex.Message}");
        }
    }

    private static async Task<(List<CodeAction> Actions, HashSet<string> CodeFixTitles)> CollectRawActionsAsync(
        Project project, Document document, Compilation compilation,
        int line, int column, int? endLine, int? endColumn, CancellationToken ct)
    {
        var sourceText = await document.GetTextAsync(ct).ConfigureAwait(false);
        var span = CreateSpan(sourceText, line, column, endLine, endColumn);
        var actions = new List<CodeAction>();
        var codeFixTitles = new HashSet<string>(StringComparer.Ordinal);

        // Collect refactoring actions
        foreach (var provider in s_refactoringProviders.Value)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var context = new CodeRefactoringContext(document, span, action => actions.Add(action), ct);
                await provider.ComputeRefactoringsAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Console.Error.WriteLineAsync(
                    $"[roslyn-codelens] Refactoring provider failed ({provider.GetType().Name}): {ex.Message}")
                    .ConfigureAwait(false);
            }
        }

        // Collect code fix actions
        var semanticModel = compilation.GetSemanticModel(
            compilation.SyntaxTrees.FirstOrDefault(t =>
                string.Equals(t.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase))
            ?? compilation.SyntaxTrees.First());

        var diagnostics = semanticModel.GetDiagnostics(span, ct);
        var syntaxTree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (syntaxTree != null)
        {
            var syntaxDiags = syntaxTree.GetDiagnostics(ct);
            diagnostics = diagnostics.AddRange(
                syntaxDiags.Where(d => d.Location.SourceSpan.IntersectsWith(span)));
        }

        if (diagnostics.Length > 0)
        {
            foreach (var provider in s_codeFixProviders.Value)
            {
                ct.ThrowIfCancellationRequested();
                var fixableIds = provider.FixableDiagnosticIds;
                var matchingDiags = diagnostics.Where(d =>
                    fixableIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase)).ToImmutableArray();

                if (matchingDiags.Length == 0)
                    continue;

                foreach (var diag in matchingDiags)
                {
                    try
                    {
                        var countBefore = actions.Count;
                        var context = new CodeFixContext(document, diag,
                            (action, _) => actions.Add(action), ct);
                        await provider.RegisterCodeFixesAsync(context).ConfigureAwait(false);

                        // Track which actions came from code fix providers
                        for (var i = countBefore; i < actions.Count; i++)
                            codeFixTitles.Add(actions[i].Title);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await Console.Error.WriteLineAsync(
                            $"[roslyn-codelens] CodeFix provider failed ({provider.GetType().Name}): {ex.Message}")
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        return (actions, codeFixTitles);
    }

    private static async Task WriteChangesToDiskAsync(
        Solution changedSolution, Solution originalSolution, CancellationToken ct)
    {
        foreach (var projectChange in changedSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var changedDocId in projectChange.GetChangedDocuments())
            {
                var changedDoc = changedSolution.GetDocument(changedDocId);
                if (changedDoc?.FilePath == null) continue;

                var text = await changedDoc.GetTextAsync(ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(changedDoc.FilePath, text.ToString(), ct).ConfigureAwait(false);
            }

            foreach (var addedDocId in projectChange.GetAddedDocuments())
            {
                var addedDoc = changedSolution.GetDocument(addedDocId);
                if (addedDoc?.FilePath == null) continue;

                var dir = Path.GetDirectoryName(addedDoc.FilePath);
                if (dir != null) Directory.CreateDirectory(dir);

                var text = await addedDoc.GetTextAsync(ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(addedDoc.FilePath, text.ToString(), ct).ConfigureAwait(false);
            }
        }
    }

    private static TextSpan CreateSpan(SourceText text, int line, int column, int? endLine, int? endColumn)
    {
        var startLine = Math.Max(0, line - 1);
        var startCol = Math.Max(0, column - 1);

        if (startLine >= text.Lines.Count)
            startLine = text.Lines.Count - 1;

        var lineInfo = text.Lines[startLine];
        var startPosition = lineInfo.Start + Math.Min(startCol, lineInfo.Span.Length);

        if (endLine.HasValue && endColumn.HasValue)
        {
            var eLine = Math.Max(0, endLine.Value - 1);
            var eCol = Math.Max(0, endColumn.Value - 1);

            if (eLine >= text.Lines.Count)
                eLine = text.Lines.Count - 1;

            var endLineInfo = text.Lines[eLine];
            var endPosition = endLineInfo.Start + Math.Min(eCol, endLineInfo.Span.Length);

            return TextSpan.FromBounds(startPosition, Math.Max(startPosition, endPosition));
        }

        return new TextSpan(startPosition, 0);
    }

    private static CodeAction? FindAction(List<CodeAction> actions, string title)
    {
        // Exact match first
        var match = actions.FirstOrDefault(a =>
            string.Equals(a.Title, title, StringComparison.Ordinal));
        if (match != null) return match;

        // Exact match case-insensitive
        match = actions.FirstOrDefault(a =>
            string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // Contains match case-insensitive
        match = actions.FirstOrDefault(a =>
            a.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // Title contains in action title
        match = actions.FirstOrDefault(a =>
            title.Contains(a.Title, StringComparison.OrdinalIgnoreCase));

        return match;
    }

    private static async Task<List<TextEdit>> ExtractTextEdits(
        Solution changedSolution, Solution originalSolution, CancellationToken ct)
    {
        var edits = new List<TextEdit>();

        foreach (var projectChange in changedSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var changedDocId in projectChange.GetChangedDocuments())
            {
                var originalDoc = originalSolution.GetDocument(changedDocId);
                var changedDoc = changedSolution.GetDocument(changedDocId);
                if (originalDoc == null || changedDoc == null) continue;

                var originalText = await originalDoc.GetTextAsync(ct).ConfigureAwait(false);
                var changedText = await changedDoc.GetTextAsync(ct).ConfigureAwait(false);
                var changes = changedText.GetTextChanges(originalText);

                foreach (var change in changes)
                {
                    var startPos = originalText.Lines.GetLinePosition(change.Span.Start);
                    var endPos = originalText.Lines.GetLinePosition(change.Span.End);

                    edits.Add(new TextEdit(
                        originalDoc.FilePath ?? "",
                        startPos.Line + 1, startPos.Character + 1,
                        endPos.Line + 1, endPos.Character + 1,
                        change.NewText ?? ""));
                }
            }

            foreach (var addedDocId in projectChange.GetAddedDocuments())
            {
                var addedDoc = changedSolution.GetDocument(addedDocId);
                if (addedDoc == null) continue;

                var text = await addedDoc.GetTextAsync(ct).ConfigureAwait(false);
                edits.Add(new TextEdit(
                    addedDoc.FilePath ?? "",
                    1, 1, 1, 1,
                    text.ToString()));
            }
        }

        return edits;
    }

    private static CodeActionInfo ToCodeActionInfo(CodeAction action, HashSet<string> codeFixTitles)
    {
        var kind = codeFixTitles.Contains(action.Title) ? "CodeFix" : "Refactoring";

        var nestedActions = action.NestedActions.IsDefaultOrEmpty
            ? null
            : (IReadOnlyList<CodeActionInfo>)action.NestedActions.Select(a => ToCodeActionInfo(a, codeFixTitles)).ToList();

        return new CodeActionInfo(action.Title, kind, nestedActions);
    }

    private static ImmutableArray<CodeRefactoringProvider> LoadBuiltInRefactoringProviders()
    {
        var providers = new List<CodeRefactoringProvider>();
        var assemblies = LoadFeaturesAssemblies();

        foreach (var assembly in assemblies)
        {
            var types = GetTypesSafe(assembly);
            if (types == null) continue;

            foreach (var type in types)
            {
                if (type.IsAbstract || !typeof(CodeRefactoringProvider).IsAssignableFrom(type))
                    continue;

                var exportAttr = type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>();
                if (exportAttr == null) continue;

                if (exportAttr.Languages != null && exportAttr.Languages.Length > 0 &&
                    !exportAttr.Languages.Contains(LanguageNames.CSharp, StringComparer.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var instance = (CodeRefactoringProvider)Activator.CreateInstance(type)!;
                    providers.Add(instance);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[roslyn-codelens] Failed to create refactoring provider '{type.Name}': {ex.Message}");
                }
            }
        }

        Console.Error.WriteLine($"[roslyn-codelens] Loaded {providers.Count} built-in refactoring providers");
        return [.. providers];
    }

    private static ImmutableArray<CodeFixProvider> LoadBuiltInCodeFixProviders()
    {
        var providers = new List<CodeFixProvider>();
        var assemblies = LoadFeaturesAssemblies();

        foreach (var assembly in assemblies)
        {
            var types = GetTypesSafe(assembly);
            if (types == null) continue;

            foreach (var type in types)
            {
                if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
                    continue;

                var exportAttr = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
                if (exportAttr == null) continue;

                if (exportAttr.Languages != null && exportAttr.Languages.Length > 0 &&
                    !exportAttr.Languages.Contains(LanguageNames.CSharp, StringComparer.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var instance = (CodeFixProvider)Activator.CreateInstance(type)!;
                    providers.Add(instance);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[roslyn-codelens] Failed to create code fix provider '{type.Name}': {ex.Message}");
                }
            }
        }

        Console.Error.WriteLine($"[roslyn-codelens] Loaded {providers.Count} built-in code fix providers");
        return [.. providers];
    }

    private static List<Assembly> LoadFeaturesAssemblies()
    {
        var assemblyNames = new[]
        {
            "Microsoft.CodeAnalysis.CSharp.Features",
            "Microsoft.CodeAnalysis.Features"
        };

        var assemblies = new List<Assembly>();
        var csharpAssemblyLocation = typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly.Location;
        var baseDir = Path.GetDirectoryName(csharpAssemblyLocation) ?? "";

        foreach (var name in assemblyNames)
        {
            try
            {
                var assembly = Assembly.Load(name);
                assemblies.Add(assembly);
            }
            catch
            {
                try
                {
                    var path = Path.Combine(baseDir, name + ".dll");
                    if (File.Exists(path))
                    {
                        var assembly = Assembly.LoadFrom(path);
                        assemblies.Add(assembly);
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"[roslyn-codelens] Features assembly not found: {name}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[roslyn-codelens] Failed to load features assembly '{name}': {ex.Message}");
                }
            }
        }

        return assemblies;
    }

    private static Type[]? GetTypesSafe(Assembly assembly)
    {
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
            Console.Error.WriteLine(
                $"[roslyn-codelens] Failed to get types from '{assembly.FullName}': {ex.Message}");
            return null;
        }
    }
}
