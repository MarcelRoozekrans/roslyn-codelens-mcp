namespace RoslynCodeLens.Models;

/// <summary>
/// One exception that can reach (or be stopped before) the analysed method's boundary.
/// Origin: <c>thrown</c> (a real throw site in source) or <c>documented</c> (an
/// <c>exception</c> XML tag on a metadata symbol).
/// </summary>
/// <remarks>
/// <para>
/// <c>HasFilter</c> describes the REPORTED outcome, not merely what was seen on the way to it.
/// It is false whenever an unconditional handler caught the exception — including when a
/// <c>when</c>-filtered clause was passed over first, because the unfiltered one catches
/// regardless. It is true only when the exception ESCAPES past one or more filtered handlers that
/// might have caught it at run time, which is the case a reader has to reason about by hand.
/// <c>Escapes: false, HasFilter: true</c> is therefore not a state this record can be in.
/// </para>
/// <para>
/// <c>Depth</c> is derived from <c>Path</c> rather than passed in, so the two cannot drift: the
/// depth of a raised exception is exactly the number of call frames between the analysed method
/// and the method that raises it.
/// </para>
/// </remarks>
public record ExceptionFlowInfo(
    string ExceptionType,
    string Origin,
    string RaisedIn,
    string File,
    int Line,
    IReadOnlyList<string> Path,
    bool Escapes,
    string? CaughtIn,
    string? CaughtFile,
    int? CaughtLine,
    bool HasFilter)
{
    public int Depth => Path.Count - 1;
}
