namespace RoslynCodeLens.Models;

/// <summary>
/// One resolved element of a pasted stack trace, in original trace order.
/// Kind: exception | method | asyncMethod | iterator | lambda | localFunction | constructor | unknown.
/// Origin: source | metadata | unresolved.
/// </summary>
public record StackFrameInfo(
    int Index,
    string Raw,
    string Kind,
    string Symbol,
    string? EnclosingMethod,
    string? File,
    int? Line,
    string Origin,
    string? Project);
