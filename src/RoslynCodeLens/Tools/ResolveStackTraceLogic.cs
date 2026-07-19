using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.StackTrace;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

/// <summary>Resolved trace plus the count of frame-like lines that failed all grammars
/// (those are also present in Frames as Kind="unknown"/Origin="unresolved" items, in order).</summary>
public sealed record StackTraceResolution(IReadOnlyList<StackFrameInfo> Frames, int SkippedFrameLike);

public static class ResolveStackTraceLogic
{
    public static StackTraceResolution Execute(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string stackTrace)
    {
        var parsed = StackTraceParser.Parse(stackTrace);
        if (parsed.Lines.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                "No stack frames recognized in input.", new { });
        }

        var frames = new List<StackFrameInfo>(parsed.Lines.Count);
        var skippedFrameLike = 0;
        foreach (var line in parsed.Lines)
        {
            if (line.IsFrameLikeUnparsed)
            {
                // Keep trace structure complete: emit the unrecognized frame-like line
                // as an unknown item at its original position.
                skippedFrameLike++;
                frames.Add(new StackFrameInfo(frames.Count, line.Raw, "unknown", line.Raw,
                    null, null, null, "unresolved", null));
                continue;
            }
            frames.Add(line.IsExceptionHeader
                ? ResolveException(resolver, metadata, line, frames.Count)
                : ResolveFrame(resolver, metadata, line, frames.Count));
        }
        return new StackTraceResolution(frames, skippedFrameLike);
    }

    private static StackFrameInfo ResolveException(
        SymbolResolver resolver, MetadataSymbolResolver metadata, ParsedTraceLine line, int index)
    {
        var typeName = line.TypeFullName; // parser emits headers already display-normalized
        var type = resolver.FindSymbols(typeName).FirstOrDefault() ?? metadata.Resolve(typeName)?.Symbol;
        var (origin, file, srcLine, project) = Locate(resolver, type);
        return new StackFrameInfo(index, line.Raw, "exception", typeName,
            null, file, srcLine, origin, project);
    }

    private static StackFrameInfo ResolveFrame(
        SymbolResolver resolver, MetadataSymbolResolver metadata, ParsedTraceLine line, int index)
    {
        var target = line.IsDemystified
            ? DemystifiedTarget(line)
            : StackFrameDemangler.Demangle(line.TypeFullName, line.MethodName);

        var method = ResolveMethod(resolver, metadata, target, line.Parameters);
        var kind = KindOf(target, method);
        var symbol = method?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)
                     ?? $"{target.TypeName}.{target.MethodName}";

        var (origin, file, srcLine, project) = Locate(resolver, method);
        // A trace-supplied location is exact — it wins over the declaration site.
        if (line.File != null)
        {
            file = line.File;
            srcLine = line.Line;
        }

        return new StackFrameInfo(index, line.Raw, kind, symbol,
            target.Kind is DemangledKind.Lambda or DemangledKind.LocalFunction ? target.EnclosingMethod : null,
            file, srcLine, origin, project);
    }

    /// <summary>Demystifier lines carry display-form names plus optional
    /// '+LocalFunc(...)' / '+(...) =&gt; { }' suffixes mapped here. Generic types keep
    /// their '&lt;TKey, TValue&gt;' blocks in the parsed text; the lookup name strips them
    /// so the arity-stripped source index matches (RuntimeTypeName keeps the original).</summary>
    private static DemangledTarget DemystifiedTarget(ParsedTraceLine line)
    {
        var runtime = TypeNameNormalizer.StripInstantiations(line.TypeFullName);
        var typeName = TypeNameNormalizer.StripAngleGenerics(TypeNameNormalizer.Normalize(line.TypeFullName));
        if (line.DemystifiedLambda)
        {
            return new DemangledTarget(typeName, line.MethodName,
                DemangledKind.Lambda, line.MethodName, null, runtime);
        }
        if (line.DemystifiedLocalFunction is { } localFunction)
        {
            return new DemangledTarget(typeName, line.MethodName,
                DemangledKind.LocalFunction, line.MethodName, localFunction, runtime);
        }
        return new DemangledTarget(typeName, line.MethodName,
            line.DemystifiedAsync ? DemangledKind.StateMachine : DemangledKind.Plain,
            null, null, runtime);
    }

    private static ISymbol? ResolveMethod(
        SymbolResolver resolver, MetadataSymbolResolver metadata, DemangledTarget target, string? parameters)
    {
        var isConstructor = target.Kind == DemangledKind.Constructor;
        IReadOnlyList<ISymbol> candidates;
        if (isConstructor)
        {
            // Constructor: resolve the type, pick a ctor by param count. '.cctor' is the
            // static (type) constructor — a distinct symbol set from InstanceConstructors.
            var type = resolver.FindSymbols(target.TypeName).OfType<INamedTypeSymbol>().FirstOrDefault();
            candidates = type == null
                ? []
                : string.Equals(target.MethodName, ".cctor", StringComparison.Ordinal)
                    ? type.StaticConstructors.Cast<ISymbol>().ToList()
                    : type.InstanceConstructors.Cast<ISymbol>().ToList();
        }
        else
        {
            candidates = resolver.FindSymbols($"{target.TypeName}.{target.MethodName}");
        }

        if (candidates.Count == 0)
            return ResolveViaMetadata(metadata, target, isConstructor);

        if (candidates.Count == 1)
            return candidates[0];

        // Overloads: prefer matching parameter count from the parsed parameter list.
        var paramCount = CountParameters(parameters);
        return candidates.OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == paramCount)
            ?? candidates[0];
    }

    /// <summary>
    /// Metadata fallback. GetTypeByMetadataName speaks the runtime name form ('+' nesting,
    /// backtick arity), so RuntimeTypeName goes first, the display-normalized name second;
    /// member form before type-only. Constructor frames skip the member form entirely —
    /// a 'T..ctor' member name can never match a metadata member lookup.
    /// </summary>
    private static ISymbol? ResolveViaMetadata(
        MetadataSymbolResolver metadata, DemangledTarget target, bool isConstructor)
    {
        var sameName = string.Equals(target.RuntimeTypeName, target.TypeName, StringComparison.Ordinal);
        if (!isConstructor)
        {
            var member = metadata.Resolve($"{target.RuntimeTypeName}.{target.MethodName}")
                ?? (sameName ? null : metadata.Resolve($"{target.TypeName}.{target.MethodName}"));
            if (member != null)
                return member.Symbol;
        }
        var type = metadata.Resolve(target.RuntimeTypeName)
            ?? (sameName ? null : metadata.Resolve(target.TypeName));
        return type?.Symbol;
    }

    /// <summary>
    /// Parameter count from the parsed parameter text: commas count only at bracket
    /// depth 0 — generic ('&lt;&gt;'), instantiation/array ('[]'), and tuple ('()') commas
    /// belong to a single parameter's type.
    /// </summary>
    private static int CountParameters(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return 0;
        var count = 1;
        var depth = 0;
        foreach (var c in parameters)
        {
            switch (c)
            {
                case '<' or '[' or '(': depth++; break;
                case '>' or ']' or ')': if (depth > 0) depth--; break;
                case ',' when depth == 0: count++; break;
            }
        }
        return count;
    }

    private static string KindOf(DemangledTarget target, ISymbol? method) => target.Kind switch
    {
        DemangledKind.Lambda => "lambda",
        DemangledKind.LocalFunction => "localFunction",
        DemangledKind.Constructor => "constructor",
        DemangledKind.StateMachine when method is IMethodSymbol ms =>
            ms.IsAsync || ms.ReturnType.Name is "Task" or "ValueTask" ? "asyncMethod"
            : ms.ReturnType.Name.StartsWith("IEnumera", StringComparison.Ordinal)
              || ms.ReturnType.Name.StartsWith("IAsyncEnumera", StringComparison.Ordinal) ? "iterator"
            : "method",
        DemangledKind.StateMachine => "unknown",
        _ => method == null ? "unknown" : "method",
    };

    private static (string Origin, string? File, int? Line, string? Project) Locate(
        SymbolResolver resolver, ISymbol? symbol)
    {
        if (symbol == null)
            return ("unresolved", null, null, null);
        if (!symbol.Locations.Any(l => l.IsInSource))
            return ("metadata", null, null, null);
        var (file, line) = resolver.GetFileAndLine(symbol);
        return ("source", file, line, resolver.GetProjectName(symbol));
    }
}
