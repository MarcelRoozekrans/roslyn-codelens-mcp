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
    [GeneratedRegex(@"^<(?<m>[^>]+)>d__\d+$")] private static partial Regex StateMachineSegment();
    [GeneratedRegex(@"^<>c(__DisplayClass[\d_]+)?$")] private static partial Regex LambdaContainer();
    [GeneratedRegex(@"^<(?<m>[^>]+)>b__[\d_]+$")] private static partial Regex LambdaMethod();
    [GeneratedRegex(@"^<(?<m>[^>]+)>g__(?<name>[^|]+)\|[\d_]+$")] private static partial Regex LocalFunctionMethod();
    [GeneratedRegex(@"`\d+")] private static partial Regex Arity();

    public static DemangledTarget Demangle(string typeFullName, string methodName)
    {
        var runtime = typeFullName;
        var segments = typeFullName.Split('+');
        var last = segments[^1];

        // async/iterator state machine: Ns.T+<M>d__N.MoveNext
        var sm = StateMachineSegment().Match(last);
        if (sm.Success && methodName is "MoveNext")
        {
            return new DemangledTarget(
                Normalize(segments[..^1]), sm.Groups["m"].Value,
                DemangledKind.StateMachine, null, null, runtime);
        }

        // lambda containers: Ns.T+<>c.<M>b__N / Ns.T+<>c__DisplayClassN_M.<M>b__K
        if (LambdaContainer().IsMatch(last))
        {
            var lm = LambdaMethod().Match(methodName);
            if (lm.Success)
            {
                var m = lm.Groups["m"].Value;
                return new DemangledTarget(
                    Normalize(segments[..^1]), m, DemangledKind.Lambda, m, null, runtime);
            }
        }

        // lambda emitted directly on the user type (static lambdas without captures)
        var direct = LambdaMethod().Match(methodName);
        if (direct.Success)
        {
            var m = direct.Groups["m"].Value;
            return new DemangledTarget(Normalize(segments), m, DemangledKind.Lambda, m, null, runtime);
        }

        // local function: Ns.T.<M>g__Name|N_M
        var lf = LocalFunctionMethod().Match(methodName);
        if (lf.Success)
        {
            return new DemangledTarget(
                Normalize(segments), lf.Groups["m"].Value,
                DemangledKind.LocalFunction, lf.Groups["m"].Value, lf.Groups["name"].Value, runtime);
        }

        if (methodName is ".ctor" or ".cctor")
            return new DemangledTarget(Normalize(segments), methodName, DemangledKind.Constructor, null, null, runtime);

        return new DemangledTarget(Normalize(segments), methodName, DemangledKind.Plain, null, null, runtime);
    }

    private static string Normalize(string[] segments)
        => Arity().Replace(string.Join('.', segments), "");
}
