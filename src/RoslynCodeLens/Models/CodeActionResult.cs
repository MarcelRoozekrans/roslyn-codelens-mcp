namespace RoslynCodeLens.Models;

/// <summary>
/// Warning is set on an otherwise successful apply when something non-fatal happened after the
/// files were already written — currently only a failed in-memory snapshot refresh. It is kept
/// separate from ErrorMessage so that "did this fail?" stays a question about Success alone.
/// </summary>
public record CodeActionResult(
    bool Success,
    string Title,
    IReadOnlyList<TextEdit> Edits,
    string? ErrorMessage = null,
    string? Warning = null);
