namespace RoslynCodeLens.Models;

public record ChangeImpact(
    string Symbol,
    int DirectReferenceCount,
    int CallerCount,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<string> AffectedProjects,
    IReadOnlyList<SymbolReference> DirectReferences,
    IReadOnlyList<CallerInfo> Callers);
