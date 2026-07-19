using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;

namespace RoslynCodeLens;

/// <summary>
/// Outcome of <see cref="SolutionChangeWriter.WriteChangesToDiskAsync"/>. Either the batch was
/// refused because files changed on disk after the solution snapshot was taken (<see cref="Written"/>
/// false, <see cref="StaleFiles"/> lists the offenders and nothing was touched), or every document
/// was written and <see cref="Documents"/> lists the written (id, text) pairs so callers can commit
/// them to the in-memory solution.
/// </summary>
public sealed record SolutionWriteResult(
    bool Written,
    IReadOnlyList<string> StaleFiles,
    IReadOnlyList<(DocumentId Id, SourceText Text)> Documents);

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

    /// <summary>
    /// Writes every changed/added document to disk as one guarded batch:
    /// <list type="number">
    /// <item>Freshness precheck (finding 1): each target's current on-disk content must still match
    /// the snapshot text the edit was computed from. Any mismatch — or a missing/unreadable file —
    /// aborts the whole batch before anything is written, returning the stale paths.</item>
    /// <item>Atomic-ish write with rollback (finding 4): texts go to a same-directory temp file
    /// first, then replace the target via <see cref="File.Move(string, string, bool)"/>; any
    /// mid-batch failure (including cancellation between files) restores every already-replaced
    /// file from bytes captured during the precheck.</item>
    /// <item>Encoding preservation (finding 8): each write uses the original document's
    /// SourceText.Encoding (falling back to the changed text's encoding, then UTF-8 without BOM),
    /// so BOM presence and charset follow what was on disk.</item>
    /// </list>
    /// </summary>
    public static async Task<SolutionWriteResult> WriteChangesToDiskAsync(
        Solution changedSolution, Solution originalSolution, CancellationToken ct)
    {
        var (plans, staleFiles) = await BuildWritePlansAsync(
            changedSolution, originalSolution, ct).ConfigureAwait(false);

        if (staleFiles.Count > 0)
            return new SolutionWriteResult(false, staleFiles, []);

        WriteAllWithRollback(plans, ct);
        return new SolutionWriteResult(true, [],
            plans.Select(p => (p.DocumentId, p.NewText)).ToList());
    }

    /// <summary>One pending file write: target path, new content, encoding to write with, and the
    /// pre-write on-disk bytes for rollback (null for added documents that had no file).</summary>
    private sealed record WritePlan(
        string FilePath, DocumentId DocumentId, SourceText NewText,
        Encoding Encoding, byte[]? OriginalBytes);

    private static async Task<(List<WritePlan> Plans, List<string> StaleFiles)> BuildWritePlansAsync(
        Solution changedSolution, Solution originalSolution, CancellationToken ct)
    {
        var plans = new List<WritePlan>();
        var staleFiles = new List<string>();

        foreach (var projectChange in changedSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var changedDocId in projectChange.GetChangedDocuments())
            {
                var originalDoc = originalSolution.GetDocument(changedDocId);
                var changedDoc = changedSolution.GetDocument(changedDocId);
                if (originalDoc?.FilePath == null || changedDoc == null) continue;

                var originalText = await originalDoc.GetTextAsync(ct).ConfigureAwait(false);
                var changedText = await changedDoc.GetTextAsync(ct).ConfigureAwait(false);

                // Read the current bytes once: they serve both the freshness comparison and,
                // if the batch later fails, a byte-exact rollback restore.
                var currentBytes = await TryReadAllBytesAsync(originalDoc.FilePath, ct).ConfigureAwait(false);
                if (currentBytes == null || !MatchesSnapshot(currentBytes, originalText))
                {
                    staleFiles.Add(originalDoc.FilePath);
                    continue;
                }

                plans.Add(new WritePlan(
                    originalDoc.FilePath, changedDocId, changedText,
                    originalText.Encoding ?? changedText.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    currentBytes));
            }

            foreach (var addedDocId in projectChange.GetAddedDocuments())
            {
                var addedDoc = changedSolution.GetDocument(addedDocId);
                if (addedDoc?.FilePath == null) continue;

                // An added document has no snapshot original: any file already at its path is
                // content the snapshot doesn't know about, so writing would clobber it — stale.
                if (File.Exists(addedDoc.FilePath))
                {
                    staleFiles.Add(addedDoc.FilePath);
                    continue;
                }

                var text = await addedDoc.GetTextAsync(ct).ConfigureAwait(false);
                plans.Add(new WritePlan(
                    addedDoc.FilePath, addedDocId, text,
                    text.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    OriginalBytes: null));
            }
        }

        return (plans, staleFiles);
    }

    /// <summary>
    /// Freshness comparison semantics: the snapshot text was itself loaded from this file
    /// (MSBuildWorkspace's file loader and the incremental rebuild both use
    /// SourceText.From(stream), which preserves line endings exactly), so an ordinal
    /// comparison would normally suffice. Newlines are still normalized on both sides first
    /// so that a line-ending-only divergence (a git autocrlf checkout or an editor EOL
    /// conversion between load and apply — plausible on Windows) doesn't spuriously veto
    /// every write: EOL drift alone is not the "someone edited this file" conflict this
    /// check exists to catch, and overwriting it with snapshot-derived text is acceptable.
    /// </summary>
    private static bool MatchesSnapshot(byte[] currentBytes, SourceText snapshotText)
    {
        using var reader = new StreamReader(
            new MemoryStream(currentBytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var diskText = reader.ReadToEnd();
        return string.Equals(
            NormalizeNewlines(diskText),
            NormalizeNewlines(snapshotText.ToString()),
            StringComparison.Ordinal);
    }

    private static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
               .Replace("\r", "\n", StringComparison.Ordinal);

    private static async Task<byte[]?> TryReadAllBytesAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Missing (FileNotFound/DirectoryNotFound) or unreadable: freshness cannot be
            // verified, so the file counts as stale and the batch aborts untouched.
            return null;
        }
    }

    /// <summary>
    /// Atomic-ish batch write: each text is written to a temp file in the target's directory,
    /// then moved over the target (a same-volume rename, so a crash cannot leave a half-written
    /// target). On any mid-batch failure — including cancellation between files — every
    /// already-replaced target is restored from its precheck-captured bytes and the temp file is
    /// deleted, so the batch either fully applies or leaves the tree as it was. Cancellation
    /// DURING a temp-file write is harmless for that file: its target hasn't been touched yet.
    /// </summary>
    private static void WriteAllWithRollback(List<WritePlan> plans, CancellationToken ct)
    {
        var replaced = new List<WritePlan>();
        string? tempPath = null;
        string? failedFile = null;
        try
        {
            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                failedFile = plan.FilePath;

                var dir = Path.GetDirectoryName(plan.FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                tempPath = plan.FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var writer = new StreamWriter(tempPath, append: false, plan.Encoding))
                    plan.NewText.Write(writer, ct);

                File.Move(tempPath, plan.FilePath, overwrite: true);
                tempPath = null;
                replaced.Add(plan);
            }
        }
        catch (Exception ex)
        {
            var rollbackFailures = RollBack(replaced, tempPath);
            var rollbackNote = rollbackFailures.Count == 0
                ? $"All {replaced.Count} previously written file(s) were rolled back; no changes remain on disk."
                : $"ROLLBACK INCOMPLETE — these files could not be restored: {string.Join(", ", rollbackFailures)}.";

            if (ex is OperationCanceledException)
            {
                throw new OperationCanceledException(
                    $"Write batch cancelled at '{failedFile}'. {rollbackNote}", ex, ct);
            }

            throw new IOException(
                $"Failed writing '{failedFile}': {ex.Message} {rollbackNote}", ex);
        }
    }

    /// <summary>Best-effort restore of already-replaced targets (and removal of a leftover temp
    /// file). Returns the paths that could not be restored; never throws.</summary>
    private static List<string> RollBack(List<WritePlan> replaced, string? tempPath)
    {
        var failures = new List<string>();

        if (tempPath != null)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException) { failures.Add(tempPath); }
            catch (UnauthorizedAccessException) { failures.Add(tempPath); }
        }

        foreach (var plan in replaced)
        {
            try
            {
                if (plan.OriginalBytes != null)
                    File.WriteAllBytes(plan.FilePath, plan.OriginalBytes);
                else
                    File.Delete(plan.FilePath);   // added document — nothing existed before
            }
            catch (IOException) { failures.Add(plan.FilePath); }
            catch (UnauthorizedAccessException) { failures.Add(plan.FilePath); }
        }

        return failures;
    }
}
