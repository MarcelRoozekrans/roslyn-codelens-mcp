namespace RoslynCodeGraph.Models;

public record SymbolReference(
    string ReferenceKind,
    string File,
    int Line,
    string Snippet,
    string Project,
    bool IsGenerated = false);
