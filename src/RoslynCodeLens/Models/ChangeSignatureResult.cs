namespace RoslynCodeLens.Models;

public record ChangeSignatureResult(
    bool Success,
    string Method,
    string OldSignature,
    string NewSignature,
    bool Applied,
    IReadOnlyList<TextEdit> Edits,
    int FilesChanged,
    IReadOnlyList<string> CascadedTo,
    IReadOnlyList<RenameConflict> Conflicts,
    string Message);
