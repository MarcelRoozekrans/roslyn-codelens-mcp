namespace RoslynCodeLens.Models;

public record SymbolReference(
    string ReferenceKind,
    string File,
    int Line,
    int Column,
    string Snippet,
    string Project,
    bool IsGenerated = false);
