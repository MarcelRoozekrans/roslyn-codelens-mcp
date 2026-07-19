namespace RoslynCodeLens.Models;

public record RenameSymbolResult(
    bool Success,
    string OldName,
    string NewName,
    bool Applied,
    IReadOnlyList<TextEdit> Edits,
    int FilesChanged,
    IReadOnlyList<RenameConflict> Conflicts,
    string Message);
