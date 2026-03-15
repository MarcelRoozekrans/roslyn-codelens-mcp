using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class AnalyzeControlFlowLogic
{
    public static async Task<ControlFlowInfo?> ExecuteAsync(
        LoadedSolution loaded, string filePath, int startLine, int endLine, CancellationToken ct)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        Document? targetDocument = null;
        Compilation? compilation = null;

        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null &&
                    doc.FilePath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    targetDocument = doc;
                    loaded.Compilations.TryGetValue(project.Id, out compilation);
                    break;
                }
            }
            if (targetDocument != null) break;
        }

        if (targetDocument == null || compilation == null)
            return null;

        var syntaxTree = await targetDocument.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
        if (syntaxTree == null) return null;

        var root = await syntaxTree.GetRootAsync(ct).ConfigureAwait(false);
        var text = await syntaxTree.GetTextAsync(ct).ConfigureAwait(false);

        if (startLine < 1 || startLine > text.Lines.Count || endLine < startLine || endLine > text.Lines.Count)
            return null;

        var startPos = text.Lines[startLine - 1].Start;
        var endPos = text.Lines[endLine - 1].End;

        var statements = root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(s => s.SpanStart >= startPos && s.Span.End <= endPos)
            .ToList();

        if (statements.Count == 0)
            return null;

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        ControlFlowAnalysis? analysis = null;

        try
        {
            analysis = semanticModel.AnalyzeControlFlow(statements[0], statements[^1]);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (analysis == null)
            return null;

        var returnStatements = analysis.ReturnStatements
            .Select(s => s.ToString().Length > 120 ? s.ToString()[..120] + "..." : s.ToString())
            .ToList();

        var exitPoints = analysis.ExitPoints
            .Select(s => s.ToString().Length > 120 ? s.ToString()[..120] + "..." : s.ToString())
            .ToList();

        return new ControlFlowInfo(
            StartPointIsReachable: analysis.StartPointIsReachable,
            EndPointIsReachable: analysis.EndPointIsReachable,
            ReturnStatements: returnStatements,
            ExitPoints: exitPoints,
            Succeeded: analysis.Succeeded);
    }
}
