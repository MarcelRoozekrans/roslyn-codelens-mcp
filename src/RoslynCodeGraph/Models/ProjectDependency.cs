namespace RoslynCodeGraph.Models;

public record ProjectDependencyGraph(
    List<ProjectRef> Direct,
    List<ProjectRef> Transitive);

public record ProjectRef(string Name, string Path);
