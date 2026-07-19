namespace RoslynCodeLens.Models;

/// <summary>A compiler error that would be introduced by applying the rename.</summary>
public record RenameConflict(string Id, string Message, string File, int Line);
