namespace RoslynCodeGraph.Models;

public record TypeHierarchy(
    List<SymbolLocation> Bases,
    List<SymbolLocation> Interfaces,
    List<SymbolLocation> Derived);
