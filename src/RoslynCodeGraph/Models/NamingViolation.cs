namespace RoslynCodeGraph.Models;

public record NamingViolation(string SymbolName, string SymbolKind, string Rule, string Suggestion, string File, int Line, string Project);
