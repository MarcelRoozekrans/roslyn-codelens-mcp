using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Disk-write path hardening (review findings 1, 4, 8): freshness precheck against the
/// snapshot, atomic-ish batch write with rollback, and encoding/BOM preservation.
/// </summary>
public class SolutionChangeWriterTests : IDisposable
{
    private readonly string _dir;

    public SolutionChangeWriterTests()
        => _dir = Directory.CreateTempSubdirectory("solution-change-writer-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* leave for the OS temp cleaner */ }
        GC.SuppressFinalize(this);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static string Source(string className)
        => $"namespace WriterDemo;\npublic class {className}\n{{\n    public int Value;\n}}\n";

    private static Solution WithReplacedText(
        Solution solution, string filePath, string oldValue, string newValue)
    {
        var docId = solution.GetDocumentIdsWithFilePath(filePath).Single();
        var text = solution.GetDocument(docId)!
            .GetTextAsync(CancellationToken.None).GetAwaiter().GetResult().ToString();
        return solution.WithDocumentText(
            docId, SourceText.From(text.Replace(oldValue, newValue, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StaleDiskFile_AbortsWholeWrite_NoFileTouched()
    {
        var pathA = PathOf("A.cs");
        var pathB = PathOf("B.cs");
        await File.WriteAllTextAsync(pathA, Source("Alpha"));
        await File.WriteAllTextAsync(pathB, Source("Beta"));
        var (loaded, _) = RenameTestWorkspace.Create(
            (pathA, Source("Alpha")), (pathB, Source("Beta")));

        // Concurrent edit lands on B AFTER the snapshot was taken.
        var drifted = Source("Beta") + "// concurrent edit\n";
        await File.WriteAllTextAsync(pathB, drifted);

        var changed = WithReplacedText(loaded.Solution, pathA, "Alpha", "Alpha2");
        changed = WithReplacedText(changed, pathB, "Beta", "Beta2");

        var result = await SolutionChangeWriter.WriteChangesToDiskAsync(
            changed, loaded.Solution, CancellationToken.None);

        Assert.False(result.Written);
        Assert.Contains(pathB, result.StaleFiles);
        Assert.Empty(result.Documents);
        // The whole batch aborts: A (which WAS fresh) must not have been written either.
        Assert.Equal(Source("Alpha"), await File.ReadAllTextAsync(pathA));
        Assert.Equal(drifted, await File.ReadAllTextAsync(pathB));
    }

    [Fact]
    public async Task MissingDiskFile_CountsAsStale()
    {
        var path = PathOf("Gone.cs");
        var (loaded, _) = RenameTestWorkspace.Create((path, Source("Gone")));
        // File was never written to disk (or was deleted since the snapshot).

        var changed = WithReplacedText(loaded.Solution, path, "Gone", "Gone2");
        var result = await SolutionChangeWriter.WriteChangesToDiskAsync(
            changed, loaded.Solution, CancellationToken.None);

        Assert.False(result.Written);
        Assert.Contains(path, result.StaleFiles);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task LineEndingOnlyDrift_IsNotStale()
    {
        // Snapshot holds LF; disk holds the same content with CRLF. The freshness
        // comparison normalizes newlines, so this must NOT abort the write.
        var path = PathOf("Eol.cs");
        var lfSource = Source("Eol");                     // built with \n
        Assert.DoesNotContain("\r", lfSource, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, lfSource.Replace("\n", "\r\n", StringComparison.Ordinal));

        var (loaded, _) = RenameTestWorkspace.Create((path, lfSource));
        var changed = WithReplacedText(loaded.Solution, path, "Eol", "Eol2");

        var result = await SolutionChangeWriter.WriteChangesToDiskAsync(
            changed, loaded.Solution, CancellationToken.None);

        Assert.True(result.Written);
        Assert.Empty(result.StaleFiles);
        Assert.Contains("class Eol2", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MidBatchFailure_RollsBackReplacedFiles_AndLeavesNoTempLitter()
    {
        var pathA = PathOf("A.cs");
        var pathB = PathOf("B.cs");
        var pathC = PathOf("C.cs");
        await File.WriteAllTextAsync(pathA, Source("Alpha"));
        await File.WriteAllTextAsync(pathB, Source("Beta"));
        await File.WriteAllTextAsync(pathC, Source("Gamma"));
        var (loaded, _) = RenameTestWorkspace.Create(
            (pathA, Source("Alpha")), (pathB, Source("Beta")), (pathC, Source("Gamma")));

        var changed = WithReplacedText(loaded.Solution, pathA, "Alpha", "Alpha2");
        changed = WithReplacedText(changed, pathB, "Beta", "Beta2");
        changed = WithReplacedText(changed, pathC, "Gamma", "Gamma2");

        // Hold C open denying writers/deleters (readers allowed, so the freshness precheck
        // can still verify it): File.Move onto C fails mid-batch with a sharing violation.
        IOException ex;
        using (new FileStream(pathC, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ex = await Assert.ThrowsAsync<IOException>(() =>
                SolutionChangeWriter.WriteChangesToDiskAsync(
                    changed, loaded.Solution, CancellationToken.None));
        }

        Assert.Contains(pathC, ex.Message, StringComparison.Ordinal);
        Assert.Contains("rolled back", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Every already-replaced file was restored; nothing on disk changed.
        Assert.Equal(Source("Alpha"), await File.ReadAllTextAsync(pathA));
        Assert.Equal(Source("Beta"), await File.ReadAllTextAsync(pathB));
        Assert.Equal(Source("Gamma"), await File.ReadAllTextAsync(pathC));
        // No temp litter: exactly the three source files remain.
        Assert.Equal(3, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public async Task PreCancelledToken_WritesNothing_AndLeavesNoTempLitter()
    {
        var path = PathOf("Cancel.cs");
        await File.WriteAllTextAsync(path, Source("Cancel"));
        var (loaded, _) = RenameTestWorkspace.Create((path, Source("Cancel")));
        var changed = WithReplacedText(loaded.Solution, path, "Cancel", "Cancel2");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SolutionChangeWriter.WriteChangesToDiskAsync(changed, loaded.Solution, cts.Token));

        Assert.Equal(Source("Cancel"), await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Utf8BomFile_KeepsBomAfterWrite()
    {
        var path = PathOf("Bom.cs");
        await File.WriteAllTextAsync(path, Source("Bom"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        // Load from disk so the document's SourceText captures the BOM'd encoding,
        // mirroring MSBuildWorkspace's file loader.
        var (loaded, _) = RenameTestWorkspace.CreateFromDisk(path);

        var changed = WithReplacedText(loaded.Solution, path, "Bom", "Bom2");
        var result = await SolutionChangeWriter.WriteChangesToDiskAsync(
            changed, loaded.Solution, CancellationToken.None);

        Assert.True(result.Written);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "UTF-8 BOM must be preserved on rewrite");
        Assert.Contains("class Bom2", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Utf8NoBomFile_StaysBomLessAfterWrite()
    {
        var path = PathOf("NoBom.cs");
        await File.WriteAllTextAsync(path, Source("NoBom"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var (loaded, _) = RenameTestWorkspace.CreateFromDisk(path);

        var changed = WithReplacedText(loaded.Solution, path, "NoBom", "NoBom2");
        var result = await SolutionChangeWriter.WriteChangesToDiskAsync(
            changed, loaded.Solution, CancellationToken.None);

        Assert.True(result.Written);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "a BOM must not be introduced on a file that had none");
        Assert.Contains("class NoBom2", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulWrite_ReturnsChangedDocumentPairs()
    {
        var pathA = PathOf("A.cs");
        var pathB = PathOf("B.cs");
        await File.WriteAllTextAsync(pathA, Source("Alpha"));
        await File.WriteAllTextAsync(pathB, Source("Beta"));
        var (loaded, _) = RenameTestWorkspace.Create(
            (pathA, Source("Alpha")), (pathB, Source("Beta")));

        // Only A changes; B stays untouched and must not appear in Documents.
        var changed = WithReplacedText(loaded.Solution, pathA, "Alpha", "Alpha2");
        var result = await SolutionChangeWriter.WriteChangesToDiskAsync(
            changed, loaded.Solution, CancellationToken.None);

        Assert.True(result.Written);
        var doc = Assert.Single(result.Documents);
        Assert.Equal(loaded.Solution.GetDocumentIdsWithFilePath(pathA).Single(), doc.Id);
        Assert.Contains("class Alpha2", doc.Text.ToString(), StringComparison.Ordinal);
    }
}
