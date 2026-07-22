using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
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
        var caller = ResolveCaller(loaded, resolver, fromProject);

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
            ? BuildConstructors(type, caller)
            : [];

        return new InstantiationOptionsResult(
            Type: type.ToDisplayString(TypeFormat),
            TypeKind: DescribeKind(type),
            Instantiable: instantiable,
            Note: note,
            Constructors: constructors,
            Factories: BuildFactories(loaded, resolver, type, caller, cancellationToken),
            // Queried by the type's own display name rather than the caller's `symbol` argument:
            // `symbol` may be a simple name that matched several types, and the DI scan must answer
            // for the one actually resolved above.
            DiRegistrations: DiRegistrationScanner.Scan(
                loaded, resolver, type.ToDisplayString(TypeFormat), cancellationToken),
            RequiredMembers: BuildRequiredMembers(type));
    }

    // ---------------------------------------------------------------- caller accessibility

    /// <summary>
    /// The viewpoint <c>accessible</c> is computed from: one project's compilation, plus a symbol
    /// inside it to stand in for "code written here".
    /// </summary>
    private sealed record CallerContext(Compilation Compilation, ISymbol Within);

    /// <summary>
    /// Null when no caller project was named — which propagates to <c>Accessible = null</c>,
    /// meaning NOT COMPUTED. It must never collapse into <c>false</c>: "you cannot call this" and
    /// "nobody asked from where" are different answers, and conflating them tells a caller a
    /// perfectly reachable public constructor is off limits.
    /// </summary>
    private static CallerContext? ResolveCaller(
        LoadedSolution loaded, SymbolResolver resolver, string? fromProject)
    {
        if (string.IsNullOrWhiteSpace(fromProject)) return null;

        var match = loaded.Compilations.FirstOrDefault(pair =>
            string.Equals(
                resolver.GetProjectName(pair.Key), fromProject, StringComparison.OrdinalIgnoreCase));

        if (match.Value is null)
            throw new McpToolException(
                ToolErrorCode.SymbolNotFound,
                $"Project '{fromProject}' not found.",
                new
                {
                    fromProject,
                    availableProjects = loaded.Compilations.Keys
                        .Select(resolver.GetProjectName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList(),
                });

        // A type declared in the project models "code written in this project" more faithfully
        // than the assembly does — it is the only context that can answer a `protected` question
        // at all. Ordered by name so the answer does not depend on symbol enumeration order; the
        // assembly is the fallback for a project that declares nothing.
        var within = SymbolResolver.GetAllTypes(match.Value.Assembly.GlobalNamespace)
            .OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal)
            .FirstOrDefault();

        return new CallerContext(match.Value, within ?? (ISymbol)match.Value.Assembly);
    }

    /// <summary>
    /// Whether the caller project could actually write this member.
    /// <para>
    /// The member arrives here as a symbol from the compilation that DECLARES it, and
    /// <see cref="Compilation.IsSymbolAccessibleWithin"/> rejects that outright — "Parameter
    /// 'symbol' must be a symbol from this compilation or some referenced assembly" — with an
    /// <c>ArgumentException</c>. So the member is first looked up again inside the caller's own
    /// compilation; only that counterpart may be asked about.
    /// </para>
    /// <para>
    /// A member with no counterpart there means the caller's project does not reference the
    /// declaring assembly at all — inaccessible, and reported as such rather than thrown.
    /// </para>
    /// </summary>
    private static bool? ComputeAccessible(CallerContext? caller, ISymbol member)
    {
        if (caller is null) return null;

        var counterpart = FindCounterpart(caller.Compilation, member);
        return counterpart is not null
               && caller.Compilation.IsSymbolAccessibleWithin(counterpart, caller.Within);
    }

    private static ISymbol? FindCounterpart(Compilation compilation, ISymbol member)
    {
        if (member.ContainingType is not { } declaring) return null;

        var localType = compilation.GetTypeByMetadataName(FullMetadataName(declaring));
        if (localType is null) return null;

        // Matched on the rendered signature rather than by SymbolEqualityComparer, for the same
        // reason the factory scan is: the two symbols belong to different compilations and are
        // never reference-equal, however identical the source.
        var key = member.ToDisplayString(SignatureFormat);
        var candidates = member is IMethodSymbol { MethodKind: MethodKind.Constructor }
            ? localType.InstanceConstructors.Cast<ISymbol>()
            : localType.GetMembers(member.Name);

        return candidates.FirstOrDefault(c =>
            string.Equals(c.ToDisplayString(SignatureFormat), key, StringComparison.Ordinal));
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

    // ---------------------------------------------------------------- factories

    /// <summary>
    /// Every static member in the solution that hands back an instance of the target type.
    /// <para>
    /// The scan is solution-wide rather than confined to the type itself because the pattern this
    /// exists for — <c>WidgetFactory.Create()</c> beside a <c>Widget</c> with a non-public
    /// constructor — puts the factory on a DIFFERENT type. A self-only scan finds nothing there,
    /// which is precisely the case where the caller has no other way in.
    /// </para>
    /// <para>
    /// INSTANCE members returning the type (a builder's <c>Build()</c>) are deliberately excluded:
    /// the builder itself would have to be constructed first, so answering "how do I construct
    /// Widget" with "call Build() on a WidgetBuilder" just restates the question one level down.
    /// </para>
    /// </summary>
    private static IReadOnlyList<FactoryOption> BuildFactories(
        LoadedSolution loaded,
        SymbolResolver resolver,
        INamedTypeSymbol type,
        CallerContext? caller,
        CancellationToken cancellationToken)
        => ScanFactories(loaded, resolver, type, caller, cancellationToken)
            .Select(static pair => pair.Option)
            .ToList();

    /// <summary>
    /// The way to construct a type whose constructors are all out of reach — which is exactly when
    /// <c>generate_test_skeleton</c> must not emit <c>new Foo()</c>. Returns the call expression
    /// for the first public, parameterless, non-obsolete, non-async static factory, or null when
    /// there is none.
    /// <para>
    /// The declaring type is spelled with its namespace so the emitted call compiles even when the
    /// factory lives somewhere the generated file does not import.
    /// </para>
    /// </summary>
    internal static string? FindParameterlessFactoryCall(
        LoadedSolution loaded,
        SymbolResolver resolver,
        INamedTypeSymbol type,
        CancellationToken cancellationToken = default)
    {
        foreach (var (member, option) in ScanFactories(
                     loaded, resolver, type, caller: null, cancellationToken))
        {
            if (member.DeclaredAccessibility != Accessibility.Public) continue;
            if (option.IsAsync || option.IsObsolete || option.Parameters.Count != 0) continue;

            // A field or property is read, not called; only a method takes the argument list.
            return member is IMethodSymbol
                ? $"{option.DeclaringType}.{member.Name}()"
                : $"{option.DeclaringType}.{member.Name}";
        }

        return null;
    }

    private static List<(ISymbol Member, FactoryOption Option)> ScanFactories(
        LoadedSolution loaded,
        SymbolResolver resolver,
        INamedTypeSymbol type,
        CallerContext? caller,
        CancellationToken cancellationToken)
    {
        // Compared as a fully-qualified STRING, never with SymbolEqualityComparer: the target was
        // resolved out of one compilation and the candidates come out of every other one, where
        // the same type is a different symbol instance. Symbol identity across compilations gives
        // false negatives, which here means silently reporting no factory at all.
        var targetKey = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var options = new List<(ISymbol Member, FactoryOption Option)>();
        var seen = new HashSet<(string DeclaringType, string Signature)>();

        foreach (var scanTree in SolutionScanner.EnumerateTrees(
                     loaded, resolver, cancellationToken: cancellationToken))
        {
            // Syntactic pre-filter: binding a tree is the expensive part of the scan, and a file
            // declaring no type can hold no static factory.
            var declarations = scanTree.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().ToList();
            if (declarations.Count == 0) continue;

            var model = scanTree.SemanticModel();
            foreach (var declaration in declarations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol container)
                    continue;

                foreach (var member in container.GetMembers())
                    if (TryBuildFactory(member, targetKey, caller) is { } factory
                        && seen.Add((factory.DeclaringType, factory.Signature)))
                        options.Add((member, factory));
            }
        }

        options.Sort(static (x, y) =>
        {
            var (a, b) = (x.Option, y.Option);
            // Source before metadata: a factory the caller can open and read is the better answer.
            var byOrigin = (a.File is null ? 1 : 0).CompareTo(b.File is null ? 1 : 0);
            if (byOrigin != 0) return byOrigin;
            var byType = string.CompareOrdinal(a.DeclaringType, b.DeclaringType);
            return byType != 0 ? byType : string.CompareOrdinal(a.Signature, b.Signature);
        });

        return options;
    }

    private static FactoryOption? TryBuildFactory(
        ISymbol member, string targetKey, CallerContext? caller)
    {
        // `static FactoryOnly Instance { get; }` emits a static field <Instance>k__BackingField
        // whose type is the target — a perfect structural match for a factory, and something no
        // caller can write. Every compiler-supplied member is excluded for that reason.
        if (member.IsImplicitlyDeclared || !member.IsStatic) return null;

        var (kind, produced, parameters) = member switch
        {
            // Ordinary only: property accessors arrive here as PropertyGet methods returning the
            // type, and would double-report every static property factory.
            IMethodSymbol { MethodKind: MethodKind.Ordinary } method =>
                ("method", method.ReturnType, (IReadOnlyList<IParameterSymbol>)method.Parameters),
            // A setter-only property hands nothing back.
            IPropertySymbol { GetMethod: not null } property =>
                ("property", property.Type, []),
            IFieldSymbol field => ("field", field.Type, []),
            _ => (null, null, []),
        };
        if (kind is null || produced is null) return null;

        var unwrapped = UnwrapAsync(produced, out var isAsync);
        if (!string.Equals(
                unwrapped.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                targetKey,
                StringComparison.Ordinal))
            return null;

        var (file, line) = SourceLocation(member);
        return new FactoryOption(
            Signature: member.ToDisplayString(SignatureFormat),
            DeclaringType: member.ContainingType.ToDisplayString(TypeFormat),
            Kind: kind,
            Accessibility: DescribeAccessibility(member.DeclaredAccessibility),
            Accessible: ComputeAccessible(caller, member),
            IsAsync: isAsync,
            IsObsolete: IsObsolete(member),
            Parameters: BuildParameters(parameters),
            File: file,
            Line: line);
    }

    /// <summary>
    /// Unwraps <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c> to <c>T</c>, so an async factory
    /// matches its target. A generic <c>static T Make&lt;T&gt;()</c> needs no special case: its
    /// return type is a type PARAMETER, whose display string never equals a concrete target's.
    /// </summary>
    private static ITypeSymbol UnwrapAsync(ITypeSymbol type, out bool isAsync)
    {
        isAsync = false;
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic)
        {
            var definition = generic.ConstructedFrom.ToDisplayString();
            if (definition.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal)
                || definition.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal))
            {
                isAsync = true;
                return generic.TypeArguments[0];
            }
        }

        return type;
    }

    // ---------------------------------------------------------------- constructors

    private static IReadOnlyList<ConstructorOption> BuildConstructors(
        INamedTypeSymbol type, CallerContext? caller)
    {
        var options = new List<ConstructorOption>();
        foreach (var ctor in type.InstanceConstructors)
        {
            if (IsCopyConstructor(ctor, type)) continue;

            var (file, line) = SourceLocation(ctor);
            options.Add(new ConstructorOption(
                Signature: ctor.ToDisplayString(SignatureFormat),
                Accessibility: DescribeAccessibility(ctor.DeclaredAccessibility),
                Accessible: ComputeAccessible(caller, ctor),
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
