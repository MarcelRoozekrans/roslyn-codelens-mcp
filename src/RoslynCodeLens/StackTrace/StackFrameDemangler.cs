using System.Text.RegularExpressions;

namespace RoslynCodeLens.StackTrace;

public enum DemangledKind { Plain, StateMachine, Lambda, LocalFunction, Constructor }

/// <summary>Logical target of a (possibly compiler-mangled) stack frame. TypeName is
/// normalized to display form: '+' nesting becomes '.', backtick arity is stripped
/// (the resolver's arity-stripped index matches it). RuntimeTypeName keeps the original
/// runtime form ('+' nesting and backtick arity preserved, generic-instantiation blocks
/// removed) for metadata lookups that speak GetTypeByMetadataName.</summary>
public sealed record DemangledTarget(
    string TypeName, string MethodName, DemangledKind Kind,
    string? EnclosingMethod, string? LocalFunctionName, string RuntimeTypeName);

public static partial class StackFrameDemangler
{
    // Classic async/iterator state machine: <M>d__N (method MoveNext)
    [GeneratedRegex(@"^<(?<m>[^>]+)>d__\d+$")] private static partial Regex StateMachineSegment();
    // Awaited async lambda: <<M>b__N_M>d, optionally with a further __N suffix (method MoveNext)
    [GeneratedRegex(@"^<<(?<m>[^>]+)>b__[\d_]+>d(?:__\d+)?$")] private static partial Regex AsyncLambdaSegment();
    // Async local function: <<M>g__Name|N(_M)?>d(__N)? (method MoveNext)
    [GeneratedRegex(@"^<<(?<m>[^>]+)>g__(?<name>[^|>]+)\|[\d_]+>d(?:__\d+)?$")] private static partial Regex AsyncLocalFunctionSegment();
    // Lambda/local-function host containers: <>c and <>c__DisplayClassN(_M)?
    [GeneratedRegex(@"^<>c(__DisplayClass[\d_]+)?$")] private static partial Regex LambdaContainer();
    // Non-async lambda method: <M>b__N(_M)?
    [GeneratedRegex(@"^<(?<m>[^>]+)>b__[\d_]+$")] private static partial Regex LambdaMethod();
    // Local function method: <M>g__Name|N(_M)? (single- or double-index suffix)
    [GeneratedRegex(@"^<(?<m>[^>]+)>g__(?<name>[^|]+)\|[\d_]+$")] private static partial Regex LocalFunctionMethod();

    public static DemangledTarget Demangle(string typeFullName, string methodName)
    {
        var runtime = TypeNameNormalizer.StripInstantiations(typeFullName);
        // Modern .NET prints nested types with '.'; Framework/Environment.StackTrace use '+'.
        // Normalize once, then treat mangled containers as trailing dotted segments.
        var segments = TypeNameNormalizer.StripArity(runtime).Replace('+', '.').Split('.');
        var last = segments[^1];

        if (methodName is "MoveNext")
        {
            // classic state machine: Ns.T.<M>d__N.MoveNext
            var sm = StateMachineSegment().Match(last);
            if (sm.Success)
            {
                return new DemangledTarget(
                    Join(StripContainers(segments[..^1])), sm.Groups["m"].Value,
                    DemangledKind.StateMachine, null, null, runtime);
            }

            // awaited async lambda: Ns.T.<>c.<<M>b__N_M>d.MoveNext
            var al = AsyncLambdaSegment().Match(last);
            if (al.Success)
            {
                var m = al.Groups["m"].Value;
                return new DemangledTarget(
                    Join(StripContainers(segments[..^1])), m,
                    DemangledKind.Lambda, m, null, runtime);
            }

            // async local function: Ns.T.<<M>g__Name|N_M>d.MoveNext
            var alf = AsyncLocalFunctionSegment().Match(last);
            if (alf.Success)
            {
                var m = alf.Groups["m"].Value;
                return new DemangledTarget(
                    Join(StripContainers(segments[..^1])), m,
                    DemangledKind.LocalFunction, m, alf.Groups["name"].Value, runtime);
            }
        }

        // non-async lambda: Ns.T[.<>c|<>c__DisplayClassN_M].<M>b__K
        var lm = LambdaMethod().Match(methodName);
        if (lm.Success)
        {
            var m = lm.Groups["m"].Value;
            return new DemangledTarget(
                Join(StripContainers(segments)), m, DemangledKind.Lambda, m, null, runtime);
        }

        // local function: Ns.T[.<>c__DisplayClassN_M].<M>g__Name|K
        var lf = LocalFunctionMethod().Match(methodName);
        if (lf.Success)
        {
            var m = lf.Groups["m"].Value;
            return new DemangledTarget(
                Join(StripContainers(segments)), m,
                DemangledKind.LocalFunction, m, lf.Groups["name"].Value, runtime);
        }

        if (methodName is ".ctor" or ".cctor")
            return new DemangledTarget(Join(segments), methodName, DemangledKind.Constructor, null, null, runtime);

        return new DemangledTarget(Join(segments), methodName, DemangledKind.Plain, null, null, runtime);
    }

    /// <summary>Strips trailing lambda-host container segments (&lt;&gt;c / &lt;&gt;c__DisplayClassN_M).</summary>
    private static string[] StripContainers(string[] segments)
    {
        var end = segments.Length;
        while (end > 0 && LambdaContainer().IsMatch(segments[end - 1])) end--;
        return segments[..end];
    }

    private static string Join(string[] segments) => string.Join('.', segments);
}
