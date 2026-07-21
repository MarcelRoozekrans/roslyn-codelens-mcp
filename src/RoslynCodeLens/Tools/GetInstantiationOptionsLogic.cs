using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Answers "how do I construct this type" — the constructors it exposes, the static factory
/// members anywhere in the solution that hand one back, and the members a caller must set.
/// <para>
/// Almost every filter here exists because Roslyn's symbol model reports members no source file
/// declares. A record has an implicit copy constructor, a struct has an implicit parameterless
/// one, an abstract class still exposes its <c>protected</c> constructors, and an auto-property
/// emits a backing field of the property's own type. Only one of those four is a construction
/// option, so "list what Roslyn gives you" is wrong in three different directions.
/// </para>
/// </summary>
public static class GetInstantiationOptionsLogic
{
    /// <summary>
    /// Constructor and factory signatures, with parameter names kept: the caller is being told
    /// what to write at a call site, and <c>Widget(string, string)</c> does not say which one is
    /// the name.
    /// </summary>
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat TypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static InstantiationOptionsResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string symbol,
        string? fromProject,
        CancellationToken cancellationToken = default)
    {
        var type = ResolveType(resolver, symbol);

        var instantiable = type.TypeKind is not TypeKind.Interface && !type.IsAbstract && !type.IsStatic;
        var note = type.TypeKind switch
        {
            TypeKind.Interface =>
                "Interfaces cannot be constructed directly — use find_implementations to find concrete types.",
            _ when type.IsStatic => "Static classes cannot be instantiated.",
            _ when type.IsAbstract =>
                "Abstract classes cannot be constructed directly — use find_implementations to find concrete subclasses.",
            _ => null,
        };

        // Deliberately NOT type.InstanceConstructors for a non-instantiable type: Roslyn happily
        // reports `protected Abs()` for an abstract class, and a caller shown a constructor will
        // try to call it.
        var constructors = instantiable
            ? BuildConstructors(type)
            : [];

        return new InstantiationOptionsResult(
            Type: type.ToDisplayString(TypeFormat),
            TypeKind: DescribeKind(type),
            Instantiable: instantiable,
            Note: note,
            Constructors: constructors,
            Factories: [],
            DiRegistrations: [],
            RequiredMembers: BuildRequiredMembers(type));
    }

    // ---------------------------------------------------------------- required members

    /// <summary>
    /// Members the caller must set in an object initializer. Base types are walked because
    /// <c>required</c> is inherited: a base's required property still has to be set by whoever
    /// constructs the derived type, and <see cref="ITypeSymbol.GetMembers()"/> on the derived type
    /// never mentions it.
    /// </summary>
    private static IReadOnlyList<RequiredMemberOption> BuildRequiredMembers(INamedTypeSymbol type)
    {
        var options = new List<RequiredMemberOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Most-derived first, so an overriding declaration wins over the base one it shadows.
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;

                var memberType = member switch
                {
                    IPropertySymbol { IsRequired: true } property => property.Type,
                    IFieldSymbol { IsRequired: true } field => field.Type,
                    _ => null,
                };
                if (memberType is null) continue;

                if (seen.Add(member.Name))
                    options.Add(new RequiredMemberOption(
                        memberType.ToDisplayString(TypeFormat), member.Name));
            }
        }

        return options;
    }

    // ---------------------------------------------------------------- constructors

    private static IReadOnlyList<ConstructorOption> BuildConstructors(INamedTypeSymbol type)
    {
        var options = new List<ConstructorOption>();
        foreach (var ctor in type.InstanceConstructors)
        {
            if (IsCopyConstructor(ctor, type)) continue;

            var (file, line) = SourceLocation(ctor);
            options.Add(new ConstructorOption(
                Signature: ctor.ToDisplayString(SignatureFormat),
                Accessibility: DescribeAccessibility(ctor.DeclaredAccessibility),
                Accessible: null,
                // Kept, not filtered: `new S()` and `new Implicit()` both compile, so a
                // compiler-supplied constructor is a real option — just one with no source to
                // point at.
                IsImplicit: ctor.IsImplicitlyDeclared,
                IsObsolete: IsObsolete(ctor),
                Parameters: BuildParameters(ctor.Parameters),
                File: file,
                Line: line));
        }

        // Fewest arguments first — that is the one a caller writing a smoke test wants — then a
        // stable tiebreak so two same-arity constructors do not swap places between runs.
        options.Sort(static (a, b) =>
        {
            var byArity = a.Parameters.Count.CompareTo(b.Parameters.Count);
            return byArity != 0 ? byArity : string.CompareOrdinal(a.Signature, b.Signature);
        });

        return options;
    }

    /// <summary>
    /// The implicit <c>protected Rec(Rec)</c> every record gets. It exists to serve <c>with</c>
    /// expressions and is never a way to construct a record you do not already have.
    /// <para>
    /// The <see cref="ISymbol.IsImplicitlyDeclared"/> test is what keeps a hand-written copy
    /// constructor — which IS a real option — from being swallowed along with it.
    /// </para>
    /// </summary>
    private static bool IsCopyConstructor(IMethodSymbol ctor, INamedTypeSymbol type)
        => ctor.IsImplicitlyDeclared
           && ctor.Parameters.Length == 1
           && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type);

    private static IReadOnlyList<ParameterOption> BuildParameters(
        IEnumerable<IParameterSymbol> parameters)
        => parameters
            .Select(p => new ParameterOption(
                p.Type.ToDisplayString(TypeFormat), p.Name, p.HasExplicitDefaultValue))
            .ToList();

    // ---------------------------------------------------------------- shared helpers

    private static INamedTypeSymbol ResolveType(SymbolResolver resolver, string symbol)
    {
        var candidates = resolver.FindNamedTypes(symbol);
        if (candidates.Count == 0)
            throw new McpToolException(
                ToolErrorCode.SymbolNotFound, $"Type '{symbol}' not found.", new { symbol });

        // A type the solution declares beats one it merely references: the question is about
        // constructing the caller's own type far more often than a metadata one.
        return candidates.FirstOrDefault(t => t.Locations.Any(l => l.IsInSource)) ?? candidates[0];
    }

    private static string DescribeKind(INamedTypeSymbol type)
    {
        var kind = type.TypeKind switch
        {
            TypeKind.Class => "class",
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => type.TypeKind.ToString().ToLowerInvariant(),
        };

        return type.IsRecord ? $"record {kind}".Replace("record class", "record", StringComparison.Ordinal) : kind;
    }

    private static string DescribeAccessibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.Private => "private",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => accessibility.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Matched by name rather than by symbol so an <c>[Obsolete]</c> declared in any assembly —
    /// including one this compilation only references — is recognised.
    /// </summary>
    private static bool IsObsolete(ISymbol symbol)
        => symbol.GetAttributes().Any(a =>
            string.Equals(a.AttributeClass?.Name, "ObsoleteAttribute", StringComparison.Ordinal));

    /// <summary>
    /// Null for implicit and metadata symbols alike — neither has a line a caller could open.
    /// </summary>
    private static (string? File, int? Line) SourceLocation(ISymbol symbol)
    {
        var location = SymbolResolver.GetLocation(symbol);
        if (location is null) return (null, null);

        var span = location.GetLineSpan();
        return (span.Path, span.StartLinePosition.Line + 1);
    }
}
