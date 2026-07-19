using System.Globalization;
using System.Text.RegularExpressions;

namespace RoslynCodeLens.StackTrace;

/// <summary>One recognized line of a pasted stack trace, before demangling/resolution.</summary>
public sealed record ParsedTraceLine(
    string Raw,
    bool IsExceptionHeader,
    string TypeFullName,      // exception type for headers; declaring type (runtime-mangled form) for frames
    string MethodName,        // empty for headers; mangled method segment for frames; [T] args stripped
    string? Parameters,       // raw parameter list text, null for headers
    string? File,
    int? Line,
    bool IsDemystified,
    bool DemystifiedAsync,
    bool IsFrameLikeUnparsed = false,          // 'at '-anchored line no grammar recognized; only Raw is meaningful
    bool DemystifiedLambda = false,            // Demystifier '+(...) => { }' suffix
    string? DemystifiedLocalFunction = null);  // Demystifier '+Name(...)' suffix

/// <summary>
/// Result of parsing a pasted stack trace. <see cref="Lines"/> is ONE ordered list covering
/// every recognized or frame-like line in original order — frame-like-but-unparsed lines are
/// discriminated via <see cref="ParsedTraceLine.IsFrameLikeUnparsed"/> so downstream consumers
/// preserve trace positions trivially. <see cref="FrameLikeUnparsed"/> is a convenience view.
/// </summary>
public sealed record StackTraceParseResult(IReadOnlyList<ParsedTraceLine> Lines)
{
    /// <summary>Frame-like lines that failed all grammars (also present in Lines, in order).</summary>
    public IReadOnlyList<string> FrameLikeUnparsed
        => Lines.Where(l => l.IsFrameLikeUnparsed).Select(l => l.Raw).ToList();
}

public static partial class StackTraceParser
{
    // "at " frame anchor anywhere in the line (log prefixes come before it).
    [GeneratedRegex(@"(?:^|\s)at\s+(?<rest>.+)$")]
    private static partial Regex AtAnchor();

    // Runtime frame body: Method-part(params)[ in file:line N]
    [GeneratedRegex(@"^(?<method>[^\s(][^(]*)\((?<params>[^)]*)\)(?:\s+in\s+(?<file>.+?):line\s+(?<line>\d+))?\s*$")]
    private static partial Regex RuntimeFrame();

    // Exception header: [---> ]Fully.Qualified.TypeName: message   (type must contain a dot,
    // no spaces — keeps "ERROR: something" log lines out).
    [GeneratedRegex(@"^(?:--->\s+)?(?<type>[A-Za-z_][A-Za-z0-9_.+`]*\.[A-Za-z0-9_.+`]+)\s*:\s+.+$")]
    private static partial Regex ExceptionHeader();

    public static StackTraceParseResult Parse(string text)
    {
        var results = new List<ParsedTraceLine>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("---", StringComparison.Ordinal) &&
                line.Contains("stack trace", StringComparison.OrdinalIgnoreCase))
                continue; // "--- End of ... stack trace ---" separators

            var at = AtAnchor().Match(line);
            if (at.Success && TryParseFrame(line, at.Groups["rest"].Value, out var frame))
            {
                results.Add(frame);
                continue;
            }

            var header = ExceptionHeader().Match(line);
            if (header.Success && !line.Contains("):", StringComparison.Ordinal))
            {
                results.Add(new ParsedTraceLine(
                    line, IsExceptionHeader: true, header.Groups["type"].Value,
                    MethodName: "", Parameters: null, File: null, Line: null,
                    IsDemystified: false, DemystifiedAsync: false));
            }
            // else: noise — dropped
        }
        return new StackTraceParseResult(results);
    }

    private static bool TryParseFrame(string raw, string body, out ParsedTraceLine frame)
    {
        frame = null!;
        var m = RuntimeFrame().Match(body);
        if (!m.Success) return false;

        var methodPart = m.Groups["method"].Value.Trim();
        var isDemystified = false;
        var demystifiedAsync = false;

        // Demystifier form: "[async ][static ]ReturnType Ns.Type.Method" — the part before
        // '(' contains spaces separating modifiers/return type from the method path.
        var lastSpace = methodPart.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            isDemystified = true;
            demystifiedAsync = methodPart.Contains("async ", StringComparison.Ordinal);
            methodPart = methodPart[(lastSpace + 1)..];
        }

        // Strip method generic args: GetById[TKey] -> GetById
        var bracket = methodPart.IndexOf('[');
        if (bracket > 0 && methodPart.EndsWith("]", StringComparison.Ordinal))
            methodPart = methodPart[..bracket];

        // Split type / method on the last '.' — but ".ctor"/".cctor" keep their leading dot.
        var splitAt = methodPart.EndsWith("..ctor", StringComparison.Ordinal) ? methodPart.Length - 5 - 1
            : methodPart.EndsWith("..cctor", StringComparison.Ordinal) ? methodPart.Length - 6 - 1
            : methodPart.LastIndexOf('.');
        if (splitAt <= 0 || splitAt == methodPart.Length - 1) return false;

        var type = methodPart[..splitAt];
        var method = methodPart[(splitAt + 1)..];

        frame = new ParsedTraceLine(
            raw, IsExceptionHeader: false, type, method,
            m.Groups["params"].Value,
            m.Groups["file"].Success ? m.Groups["file"].Value : null,
            m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture) : null,
            isDemystified, demystifiedAsync);
        return true;
    }
}
