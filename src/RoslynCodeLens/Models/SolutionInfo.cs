namespace RoslynCodeLens.Models;

public sealed record SolutionInfo(
    string Path,
    bool IsActive,
    int ProjectCount,
    string Status,
    IReadOnlyList<SkippedProjectInfo> SkippedProjects);

public sealed record SkippedProjectInfo(string Name, string Path, string Kind, string Reason);
