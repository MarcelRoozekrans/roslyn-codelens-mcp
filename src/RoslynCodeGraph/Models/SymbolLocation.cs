namespace RoslynCodeGraph.Models;

public record SymbolLocation(
    string Type,       // "class", "struct", "record"
    string FullName,
    string File,
    int Line,
    string Project,
    bool IsGenerated = false);
