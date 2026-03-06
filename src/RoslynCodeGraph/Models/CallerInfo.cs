namespace RoslynCodeGraph.Models;

public record CallerInfo(
    string Caller,
    string File,
    int Line,
    string Snippet,
    string Project);
