namespace RoslynCodeLens.Models;

/// <summary>
/// One requested member's source (or why it couldn't be returned).
/// Status: ok | notFound | ambiguous | metadata | unsupportedKind.
/// Kind (ok items): method | constructor | property | indexer | field | event.
/// </summary>
public record MemberSourceInfo(
    string RequestedSymbol,
    string Status,
    string? Symbol,
    string? Kind,
    string? File,
    int? StartLine,
    int? EndLine,
    string? Source,
    string? Project,
    IReadOnlyList<string>? Candidates = null);
