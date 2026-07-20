namespace RoslynCodeLens.Models;

/// <summary>
/// One exception that can reach (or be stopped before) the analysed method's boundary.
/// Origin: <c>thrown</c> (a real throw site in source) or <c>documented</c> (an
/// <c>exception</c> XML tag on a metadata symbol). HasFilter means the matching catch has a
/// <c>when</c> clause, so it may decline at runtime — such an exception is reported as still
/// escaping.
/// </summary>
public record ExceptionFlowInfo(
    string ExceptionType,
    string Origin,
    string RaisedIn,
    string File,
    int Line,
    int Depth,
    IReadOnlyList<string> Path,
    bool Escapes,
    string? CaughtIn,
    string? CaughtFile,
    int? CaughtLine,
    bool HasFilter);
