using Microsoft.CodeAnalysis;
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
    /// Reduced, call-site form without the receiver prefix: <c>Where&lt;int&gt;(Func&lt;int, bool&gt;)</c>.
    /// </summary>
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat;

    public static IReadOnlyList<ExtensionMemberInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string type,
        string? nameFilter)
    {
        var (receiver, compilation) = ResolveReceiver(loaded, resolver, metadata, type);

        var results = new List<ExtensionMemberInfo>();
        foreach (var candidate in CandidateContainers(compilation))
        {
            CollectMethods(candidate, receiver, results);
            CollectBlockProperties(candidate, receiver, results);
        }

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
    /// Pass A — ordinary extension methods. C# 14 block <em>methods</em> arrive here for free:
    /// the compiler lifts them onto the containing static class with IsExtensionMethod set.
    /// </summary>
    private static void CollectMethods(
        INamedTypeSymbol container, ITypeSymbol receiver, List<ExtensionMemberInfo> results)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is not IMethodSymbol { IsExtensionMethod: true } method) continue;

            var reduced = method.ReduceExtensionMethod(receiver);
            if (reduced is null) continue;

            results.Add(Build(
                reduced.Name,
                "method",
                reduced.ToDisplayString(SignatureFormat),
                container,
                method));
        }
    }

    /// <summary>
    /// Pass B — C# 14 extension <em>properties</em>. They never appear as extension methods: the
    /// containing class exposes only a <c>get_X</c> with IsExtensionMethod false. They are
    /// reachable through the containing class's nested <see cref="INamedTypeSymbol.IsExtension"/>
    /// type (which has an empty name), whose properties reduce via ReduceExtensionMember.
    /// </summary>
    private static void CollectBlockProperties(
        INamedTypeSymbol container, ITypeSymbol receiver, List<ExtensionMemberInfo> results)
    {
        foreach (var nested in container.GetTypeMembers())
        {
            if (!nested.IsExtension) continue;

            foreach (var member in nested.GetMembers())
            {
                if (member is not IPropertySymbol property) continue;

                var reduced = property.ReduceExtensionMember(receiver);
                if (reduced is null) continue;

                results.Add(Build(
                    reduced.Name,
                    "property",
                    $"{reduced.Type.ToDisplayString(TypeFormat)} {reduced.Name}",
                    container,
                    property));
            }
        }
    }

    private static ExtensionMemberInfo Build(
        string name, string kind, string signature, INamedTypeSymbol container, ISymbol declaration)
    {
        var location = SymbolResolver.GetLocation(declaration);
        var span = location?.GetLineSpan();

        return new ExtensionMemberInfo(
            Name: name,
            Kind: kind,
            Signature: signature,
            DeclaringType: container.ToDisplayString(),
            Namespace: container.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            Origin: location is null ? "metadata" : "source",
            // Read off the ORIGINAL declaration, not the reduced symbol: reduction rewrites the
            // member as seen from the receiver, and a static extension member is still invoked on
            // the type (`int.Zero`) rather than on an instance.
            IsStatic: declaration.IsStatic,
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
    /// Resolves the receiver type <em>inside each compilation</em> and picks the one that
    /// declares it, so candidate scope is the receiver's own project. Resolving once and then
    /// hunting for a compilation would pick whichever compilation the shared symbol index
    /// happened to cache — for a type referenced by a downstream project that is the wrong one,
    /// and every extension visible only downstream would be falsely reported.
    /// </summary>
    private static (ITypeSymbol Receiver, Compilation Compilation) ResolveReceiver(
        LoadedSolution loaded, SymbolResolver resolver, MetadataSymbolResolver metadata, string type)
    {
        var compilations = loaded.Compilations.Values
            .OrderBy(c => c.AssemblyName, StringComparer.Ordinal)
            .ToList();

        (ITypeSymbol, Compilation)? fallback = null;
        foreach (var compilation in compilations)
        {
            if (ResolveInCompilation(compilation, resolver, type) is not { } resolved) continue;

            var declaredHere = resolved.Locations.Any(l => l.IsInSource)
                && string.Equals(
                    resolved.ContainingAssembly?.Name, compilation.Assembly.Name, StringComparison.Ordinal);
            if (declaredHere)
                return (resolved, compilation);

            fallback ??= (resolved, compilation);
        }

        if (fallback is { } hit)
            return hit;

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

    private static ITypeSymbol? ResolveInCompilation(
        Compilation compilation, SymbolResolver resolver, string type)
    {
        var name = type.Trim();
        if (name.Length == 0) return null;

        if (Keywords.TryGetValue(name, out var special))
            return compilation.GetSpecialType(special);

        if (name.EndsWith('>') && name.IndexOf('<', StringComparison.Ordinal) > 0)
        {
            var open = name.IndexOf('<', StringComparison.Ordinal);
            var argNames = SplitTypeArguments(name[(open + 1)..^1]);
            var definition = ResolveNamedType(compilation, resolver, name[..open], argNames.Count);
            if (definition is null) return null;

            var args = new ITypeSymbol[argNames.Count];
            for (var i = 0; i < argNames.Count; i++)
            {
                if (ResolveInCompilation(compilation, resolver, argNames[i]) is not { } arg)
                    return null;
                args[i] = arg;
            }

            return definition.Construct(args);
        }

        return ResolveNamedType(compilation, resolver, name, arity: 0);
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

    private static List<string> SplitTypeArguments(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    parts.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
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
