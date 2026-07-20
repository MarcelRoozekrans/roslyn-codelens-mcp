namespace RoslynCodeLens.Models;

/// <summary>
/// One concrete source location where the violating dependency is expressed.
/// </summary>
public record ViolationSite(string File, int Line, int Column, string SourceSymbol, string TargetSymbol);

/// <summary>
/// One violated (rule, sourceScope → targetScope) edge. ReferenceCount is the full count;
/// Sites carries only the first maxSitesPerViolation of them. RuleIndex is the position of the
/// rule in the caller's own `rules` array, so a violation is always traceable back to the rule
/// as written — several `to` patterns of one rule stay one rule.
/// </summary>
public record ArchitectureViolation(
    int RuleIndex,
    string RuleKind,
    string? RuleDescription,
    string FromPattern,
    string ToPattern,
    string SourceScope,
    string TargetScope,
    int ReferenceCount,
    IReadOnlyList<ViolationSite> Sites);
