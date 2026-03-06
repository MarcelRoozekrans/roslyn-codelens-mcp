namespace RoslynCodeGraph.Models;

public record SymbolContext(
    string FullName,
    string Namespace,
    string Project,
    string File,
    int Line,
    string? BaseClass,
    List<string> Interfaces,
    List<string> InjectedDependencies,
    List<string> PublicMembers);
