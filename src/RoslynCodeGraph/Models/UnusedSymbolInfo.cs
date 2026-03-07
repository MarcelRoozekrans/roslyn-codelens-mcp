namespace RoslynCodeGraph.Models;

public record UnusedSymbolInfo(
    string SymbolName,
    string SymbolKind,
    string File,
    int Line,
    string Project,
    bool IsGenerated = false);
