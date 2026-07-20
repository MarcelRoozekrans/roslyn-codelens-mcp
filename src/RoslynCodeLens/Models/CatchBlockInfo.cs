namespace RoslynCodeLens.Models;

/// <summary>
/// CaughtType is null for a bare <c>catch</c>. Rethrows/IsEmpty answer "is this swallowing?".
/// </summary>
public record CatchBlockInfo(
    string? CaughtType,
    string Method,
    string File,
    int Line,
    int Column,
    bool HasFilter,
    bool Rethrows,
    bool IsEmpty,
    string Snippet,
    string Project);
