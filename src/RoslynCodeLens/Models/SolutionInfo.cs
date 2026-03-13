namespace RoslynCodeLens.Models;

public sealed record SolutionInfo(
    string Path,
    bool IsActive,
    int ProjectCount,
    string Status);
