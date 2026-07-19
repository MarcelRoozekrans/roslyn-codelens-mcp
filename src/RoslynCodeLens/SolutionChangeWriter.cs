using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens;

/// <summary>
/// Shared write path for tools that produce a changed Solution (apply_code_action,
/// rename_symbol): diff extraction for previews and document writes for apply mode.
/// </summary>
public static class SolutionChangeWriter
{
    public static async Task<List<TextEdit>> ExtractTextEditsAsync(
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

    public static async Task WriteChangesToDiskAsync(
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
}
