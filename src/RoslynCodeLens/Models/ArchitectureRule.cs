namespace RoslynCodeLens.Models;

/// <summary>
/// One layering rule. Kind: forbid | allowOnly.
/// forbid    → a dependency from <c>From</c> to any scope matching <c>To</c> is a violation.
/// allowOnly → a dependency from <c>From</c> to any solution-internal scope NOT in <c>To</c> is a violation.
/// Patterns are exact ("Demo.Domain"), prefix-wildcard ("Demo.Domain.*"), or "*".
/// </summary>
public record ArchitectureRule(
    string Kind,
    string From,
    IReadOnlyList<string> To,
    string? Description = null);
