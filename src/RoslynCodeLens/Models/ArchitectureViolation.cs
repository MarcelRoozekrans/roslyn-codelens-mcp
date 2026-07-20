namespace RoslynCodeLens.Models;

/// <summary>
/// One concrete source location where the violating dependency is expressed.
/// </summary>
public record ViolationSite(string File, int Line, int Column, string SourceSymbol, string TargetSymbol);

/// <summary>
/// One violated (rule, sourceScope → targetScope) edge. ReferenceCount is the full count;
/// Sites carries only the first maxSitesPerViolation of them.
/// </summary>
public record ArchitectureViolation(
    string RuleKind,
    string? RuleDescription,
    string FromPattern,
    string ToPattern,
    string SourceScope,
    string TargetScope,
    int ReferenceCount,
    IReadOnlyList<ViolationSite> Sites);
