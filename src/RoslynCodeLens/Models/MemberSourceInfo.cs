namespace RoslynCodeLens.Models;

/// <summary>
/// One requested member's source (or why it couldn't be returned).
/// Status: ok | notFound | ambiguous | metadata | unsupportedKind.
/// Kind (ok items): method | constructor | property | indexer | field | event.
/// Origin (metadata items): which assembly defines the member, so agents know
/// where to point peek_il / inspect_external_assembly.
/// Note: human-readable explanation for non-ok statuses (what happened, what to use instead).
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
    IReadOnlyList<string>? Candidates = null,
    SymbolOrigin? Origin = null,
    string? Note = null);
