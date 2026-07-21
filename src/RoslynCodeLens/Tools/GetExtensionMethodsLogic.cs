using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Answers "which extension members apply to this type", over solution source AND referenced
/// metadata (LINQ), including C# 14 <c>extension</c> blocks.
/// <para>
/// Applicability is decided by Roslyn, not by name matching: <see cref="IMethodSymbol.ReduceExtensionMethod"/>
/// and <see cref="IPropertySymbol.ReduceExtensionMember"/> return null when the member does not
/// apply, and otherwise return the member <em>as called</em> — generic inference already resolved.
/// That is what makes <c>this IEnumerable&lt;T&gt;</c> apply to <c>string</c> while
/// <c>this IEnumerable&lt;string&gt;</c> does not.
/// </para>
/// </summary>
public static class GetExtensionMethodsLogic
{
    /// <summary>
    /// Reduced, call-site form without the receiver prefix, return type first:
    /// <c>IEnumerable&lt;int&gt; Where&lt;int&gt;(Func&lt;int, bool&gt;)</c> for a method,
    /// <c>int Tripled</c> for a property. One format for both kinds — a listing that prints
    /// <c>int Tripled</c> beside <c>Thrice()</c> reads as if one of them is broken.
    /// <para>
    /// This is the call-site form, NOT literally paste-ready source: a signature names parameter
    /// <em>types</em>, never arguments, and a partially-inferred generic keeps the type parameters
    /// the compiler still has to infer from the arguments (<c>Select&lt;int, TResult&gt;</c>).
    /// Dropping the type-argument list would not make it compile either, and would throw away the
    /// genuinely useful half — that <c>TSource</c> is already pinned to <c>int</c>.
    /// </para>
    /// </summary>
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static IReadOnlyList<ExtensionMemberInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string type,
        string? nameFilter)
    {
        var receivers = ResolveReceivers(loaded, resolver, metadata, type);

        var results = new List<ExtensionMemberInfo>();
        foreach (var (receiver, compilation) in receivers)
        {
            foreach (var candidate in CandidateContainers(compilation))
            {
                CollectMethods(candidate, receiver, results);
                CollectBlockMembers(candidate, receiver, results);
            }
        }

        // Several compilations only ever contribute for a metadata receiver, and they overlap
        // heavily — every project references the same BCL, so every LINQ member is found once per
        // project. The records are value-equal for the same member, which is exactly the key.
        if (receivers.Count > 1)
            results = results.Distinct().ToList();

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            results = results
                .Where(r => r.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        results.Sort(static (a, b) =>
        {
            // Source first: the solution's own handful of extensions is usually the answer.
            var byOrigin = OriginRank(a.Origin).CompareTo(OriginRank(b.Origin));
            if (byOrigin != 0) return byOrigin;
            var byType = string.CompareOrdinal(a.DeclaringType, b.DeclaringType);
            if (byType != 0) return byType;
            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.Signature, b.Signature);
        });

        return results;
    }

    private static int OriginRank(string origin)
        => string.Equals(origin, "source", StringComparison.Ordinal) ? 0 : 1;

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// Candidate containers are the receiver compilation's own static classes plus those of the
    /// assemblies it references. An extension in a project the receiver's project does not
    /// reference is not callable from there, so reporting it would be a false positive.
    /// <para>
    /// <see cref="INamedTypeSymbol.MightContainExtensionMethods"/> prunes the vast majority of
    /// metadata types before their members are decoded, which is what keeps a full-framework
    /// scan (~175 assemblies, ~12k types) in the tens of milliseconds once symbols are warm.
    /// It is true for classes holding only extension <em>properties</em> too.
    /// </para>
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> CandidateContainers(Compilation compilation)
    {
        foreach (var t in SymbolResolver.GetAllTypes(compilation.Assembly.GlobalNamespace))
            if (IsExtensionContainer(t))
                yield return t;

        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!reference.MightContainExtensionMethods) continue;
            foreach (var t in SymbolResolver.GetAllTypes(reference.GlobalNamespace))
                if (IsExtensionContainer(t) && t.DeclaredAccessibility == Accessibility.Public)
                    yield return t;
        }
    }

    private static bool IsExtensionContainer(INamedTypeSymbol type)
        => type.IsStatic && type.TypeKind == TypeKind.Class && type.MightContainExtensionMethods;

    /// <summary>
    /// Pass A — ordinary extension methods, i.e. everything callable on an INSTANCE. C# 14 block
    /// <em>instance</em> methods arrive here for free: the compiler lifts them onto the containing
    /// static class with IsExtensionMethod set, because they do have a classic call form.
    /// </summary>
    private static void CollectMethods(
        INamedTypeSymbol container, ITypeSymbol receiver, List<ExtensionMemberInfo> results)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is not IMethodSymbol { IsExtensionMethod: true } method) continue;

            var reduced = method.ReduceExtensionMethod(receiver);
            if (reduced is null) continue;

            results.Add(Build(reduced, "method", container, method));
        }
    }

    /// <summary>
    /// Pass B — the C# 14 block members pass A cannot see, reached through the containing class's
    /// nested <see cref="INamedTypeSymbol.IsExtension"/> type (which has an empty name):
    /// <list type="bullet">
    /// <item>every extension <em>property</em>, static or not — the container exposes only a
    /// <c>get_X</c> with IsExtensionMethod false, so an IsExtensionMethod scan misses them all;</item>
    /// <item>static extension <em>methods</em> — <c>int.MakeZero()</c> has no classic call form,
    /// so unlike an instance block method the container's copy has IsExtensionMethod false too.</item>
    /// </list>
    /// <para>
    /// Instance block methods are deliberately skipped here: they appear in BOTH the container
    /// (lifted, reported by pass A) and this nested type, and IsExtensionMethod is false on the
    /// nested copy, so anything but an IsStatic test double-reports them. Property accessors
    /// (<c>get_X</c>/<c>set_X</c>) are skipped too — they are reported as their property.
    /// </para>
    /// </summary>
    private static void CollectBlockMembers(
        INamedTypeSymbol container, ITypeSymbol receiver, List<ExtensionMemberInfo> results)
    {
        foreach (var nested in container.GetTypeMembers())
        {
            if (!nested.IsExtension) continue;

            foreach (var member in nested.GetMembers())
            {
                switch (member)
                {
                    case IPropertySymbol property
                        when property.ReduceExtensionMember(receiver) is { } reducedProperty:
                        results.Add(Build(reducedProperty, "property", container, property));
                        break;

                    case IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: true } method
                        when method.ReduceExtensionMember(receiver) is { } reducedMethod:
                        results.Add(Build(reducedMethod, "method", container, method));
                        break;
                }
            }
        }
    }

    private static ExtensionMemberInfo Build(
        ISymbol reduced, string kind, INamedTypeSymbol container, ISymbol declaration)
    {
        var location = SymbolResolver.GetLocation(declaration);
        var span = location?.GetLineSpan();

        return new ExtensionMemberInfo(
            Name: reduced.Name,
            Kind: kind,
            Signature: reduced.ToDisplayString(SignatureFormat),
            DeclaringType: container.ToDisplayString(),
            Namespace: container.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            Origin: location is null ? "metadata" : "source",
            // Off the REDUCED symbol, which is the only one that answers the question the field
            // actually asks — "is this invoked on the type or on an instance?". The declaration
            // cannot answer it: a classic `this int` extension is declared static like every other
            // extension method, yet is called `value.Doubled()`. Reduction models the call site, so
            // `Doubled` and a block instance method come back IsStatic false while a block's own
            // `static int Zero` stays true.
            IsStatic: reduced.IsStatic,
            File: span?.Path,
            Line: span is null ? null : span.Value.StartLinePosition.Line + 1,
            XmlDocSummary: MethodDisplayHelpers.ExtractSummary(declaration));
    }

    // ---------------------------------------------------------------- receiver resolution

    private static readonly Dictionary<string, SpecialType> Keywords = new(StringComparer.Ordinal)
    {
        ["bool"] = SpecialType.System_Boolean,
        ["byte"] = SpecialType.System_Byte,
        ["sbyte"] = SpecialType.System_SByte,
        ["char"] = SpecialType.System_Char,
        ["decimal"] = SpecialType.System_Decimal,
        ["double"] = SpecialType.System_Double,
        ["float"] = SpecialType.System_Single,
        ["int"] = SpecialType.System_Int32,
        ["uint"] = SpecialType.System_UInt32,
        ["long"] = SpecialType.System_Int64,
        ["ulong"] = SpecialType.System_UInt64,
        ["short"] = SpecialType.System_Int16,
        ["ushort"] = SpecialType.System_UInt16,
        ["object"] = SpecialType.System_Object,
        ["string"] = SpecialType.System_String,
    };

    /// <summary>
    /// Resolves the receiver type <em>inside each compilation</em>, because candidate scope is
    /// decided per compilation. Resolving once and then hunting for a compilation would pick
    /// whichever compilation the shared symbol index happened to cache — for a type referenced by
    /// a downstream project that is the wrong one, and every extension visible only downstream
    /// would be falsely reported.
    /// <para>
    /// A type the solution DECLARES has exactly one right answer: its own project, whose reference
    /// closure is what is callable there. A type it merely REFERENCES — <c>string</c>, <c>int</c>,
    /// any BCL type — is declared by no project, so there is no such thing as "its compilation".
    /// Picking one would silently drop the solution's own extensions on <c>string</c> declared in
    /// every other project, and any project's extension on <c>string</c> is a legitimate answer to
    /// "what can I call on a string?". So metadata receivers gather ALL compilations.
    /// </para>
    /// </summary>
    private static IReadOnlyList<(ITypeSymbol Receiver, Compilation Compilation)> ResolveReceivers(
        LoadedSolution loaded, SymbolResolver resolver, MetadataSymbolResolver metadata, string type)
    {
        var compilations = loaded.Compilations.Values
            .OrderBy(c => c.AssemblyName, StringComparer.Ordinal)
            .ToList();

        var referencing = new List<(ITypeSymbol, Compilation)>();
        foreach (var compilation in compilations)
        {
            if (ResolveInCompilation(compilation, resolver, type) is not { } resolved) continue;

            var declaredHere = resolved.Locations.Any(l => l.IsInSource)
                && string.Equals(
                    resolved.ContainingAssembly?.Name, compilation.Assembly.Name, StringComparison.Ordinal);
            if (declaredHere)
                return [(resolved, compilation)];

            referencing.Add((resolved, compilation));
        }

        if (referencing.Count > 0)
            return referencing;

        // Nothing resolved as a type. Distinguish "you named a namespace / a member" from
        // "no such thing" so the caller gets an actionable error.
        var nonType = resolver.FindSymbols(type).FirstOrDefault(s => s is not ITypeSymbol)
            ?? metadata.Resolve(type)?.Symbol as ISymbol;
        if (nonType is not null && nonType is not ITypeSymbol)
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"'{type}' is a {nonType.Kind.ToString().ToLowerInvariant()}, not a type.",
                new { type });

        if (compilations.Any(c => FindNamespace(c, type) is not null))
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"'{type}' is a namespace, not a type. Pass a type name.",
                new { type });

        throw new McpToolException(
            ToolErrorCode.SymbolNotFound, $"Type '{type}' not found.", new { type });
    }

    /// <summary>
    /// Roslyn's own parser supplies the SHAPE of the type name; only the leaf names are looked up
    /// here. That is what makes <c>string[]</c>, <c>int?</c>, <c>(int, string)</c>, <c>int[,]</c>
    /// and combinations of them work — "what can I call on this array" is one of the questions the
    /// tool exists to answer, and Roslyn reduces against those receivers perfectly well; only the
    /// hand-rolled name parser this replaced could not spell them.
    /// <para>
    /// Leaves still go through <see cref="ResolveNamedType"/> rather than a semantic model, so a
    /// bare <c>Widget</c> keeps resolving through the solution-wide index — speculative binding
    /// would need a call-site position the tool does not have, and would only handle names already
    /// in scope at whatever position was invented for it.
    /// </para>
    /// </summary>
    private static ITypeSymbol? ResolveInCompilation(
        Compilation compilation, SymbolResolver resolver, string type)
    {
        var name = type.Trim();
        if (name.Length == 0) return null;

        var syntax = SyntaxFactory.ParseTypeName(name);
        // ParseTypeName never fails outright — it consumes a prefix and reports diagnostics for the
        // rest — so reject anything that is not entirely a type name ("Widget foo", "int)").
        if (syntax.ContainsDiagnostics || syntax.FullSpan.End != name.Length) return null;

        return ResolveSyntax(compilation, resolver, syntax);
    }

    private static ITypeSymbol? ResolveSyntax(
        Compilation compilation, SymbolResolver resolver, TypeSyntax syntax)
    {
        switch (syntax)
        {
            case ArrayTypeSyntax array:
            {
                if (ResolveSyntax(compilation, resolver, array.ElementType) is not { } element)
                    return null;

                // Rank specifiers are written outermost-first: `string[][]` is an array of
                // `string[]`, so they have to be applied inside out.
                var result = element;
                for (var i = array.RankSpecifiers.Count - 1; i >= 0; i--)
                    result = compilation.CreateArrayTypeSymbol(result, array.RankSpecifiers[i].Rank);
                return result;
            }

            case NullableTypeSyntax nullable:
            {
                if (ResolveSyntax(compilation, resolver, nullable.ElementType) is not { } element)
                    return null;

                // `string?` is `string` — a nullable reference annotation, not `Nullable<string>`,
                // and annotations do not change which extensions apply.
                return element.IsValueType
                    ? compilation.GetSpecialType(SpecialType.System_Nullable_T).Construct(element)
                    : element;
            }

            case TupleTypeSyntax tuple:
            {
                if (tuple.Elements.Count < 2) return null;

                var types = ImmutableArray.CreateBuilder<ITypeSymbol>(tuple.Elements.Count);
                var names = ImmutableArray.CreateBuilder<string?>(tuple.Elements.Count);
                var named = false;
                foreach (var element in tuple.Elements)
                {
                    if (ResolveSyntax(compilation, resolver, element.Type) is not { } elementType)
                        return null;
                    types.Add(elementType);
                    var elementName = element.Identifier.ValueText;
                    named |= elementName.Length > 0;
                    names.Add(elementName.Length > 0 ? elementName : null);
                }

                return compilation.CreateTupleTypeSymbol(
                    types.MoveToImmutable(), named ? names.MoveToImmutable() : default);
            }

            case PredefinedTypeSyntax predefined:
                return Keywords.TryGetValue(predefined.Keyword.ValueText, out var special)
                    ? compilation.GetSpecialType(special)
                    : null;

            case GenericNameSyntax generic:
                return ResolveGeneric(compilation, resolver, generic, generic.Identifier.ValueText);

            case QualifiedNameSyntax { Right: GenericNameSyntax rightGeneric } qualified:
                return ResolveGeneric(
                    compilation, resolver, rightGeneric,
                    $"{qualified.Left}.{rightGeneric.Identifier.ValueText}");

            case QualifiedNameSyntax or AliasQualifiedNameSyntax:
                return ResolveNamedType(compilation, resolver, syntax.ToString(), arity: 0);

            case IdentifierNameSyntax identifier:
                return ResolveNamedType(
                    compilation, resolver, identifier.Identifier.ValueText, arity: 0);

            default:
                return null;
        }
    }

    private static ITypeSymbol? ResolveGeneric(
        Compilation compilation, SymbolResolver resolver, GenericNameSyntax generic, string name)
    {
        var arguments = generic.TypeArgumentList.Arguments;
        var definition = ResolveNamedType(compilation, resolver, name, arguments.Count);
        if (definition is null) return null;

        var args = new ITypeSymbol[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            if (ResolveSyntax(compilation, resolver, arguments[i]) is not { } arg) return null;
            args[i] = arg;
        }

        return definition.Construct(args);
    }

    private static INamedTypeSymbol? ResolveNamedType(
        Compilation compilation, SymbolResolver resolver, string name, int arity)
    {
        var direct = compilation.GetTypeByMetadataName(arity == 0 ? name : $"{name}`{arity}");
        if (direct is not null) return direct;

        // Simple or partially qualified names ("Widget", "List") go through the solution-wide
        // index for their metadata name, then get looked up in THIS compilation so scoping holds.
        foreach (var indexed in resolver.FindNamedTypes(name))
        {
            if (indexed.Arity != arity) continue;
            var here = compilation.GetTypeByMetadataName(FullMetadataName(indexed));
            if (here is not null) return here;
        }

        return null;
    }

    private static string FullMetadataName(INamedTypeSymbol type)
    {
        var parts = new List<string> { type.MetadataName };
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            parts.Insert(0, outer.MetadataName);

        var name = string.Join('+', parts);
        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? name : $"{ns.ToDisplayString()}.{name}";
    }

    private static INamespaceSymbol? FindNamespace(Compilation compilation, string name)
    {
        INamespaceSymbol? current = compilation.GlobalNamespace;
        foreach (var segment in name.Split('.'))
        {
            current = current?.GetNamespaceMembers()
                .FirstOrDefault(n => string.Equals(n.Name, segment, StringComparison.Ordinal));
            if (current is null) return null;
        }

        return current;
    }
}
