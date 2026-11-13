namespace RoslynCodeLens.Models;

/// <param name="Warnings">
/// What the change affects beyond the rewritten call sites and that Roslyn's engine does not
/// rewrite. Empty for methods; for a positional record's primary constructor it names removed
/// generated properties with their references, deconstructions and positional patterns that
/// follow the new order, and the text the change leaves untouched.
/// </param>
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
    string Message,
    IReadOnlyList<string> Warnings);
