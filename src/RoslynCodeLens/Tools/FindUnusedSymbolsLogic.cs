using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindUnusedSymbolsLogic
{
    public static (IReadOnlyList<UnusedSymbolInfo> Items, IReadOnlyDictionary<string, int> FilteredCounts) Execute(
        LoadedSolution loaded, SymbolResolver resolver, string? project, bool includeInternal)
    {
        var referencedSymbols = CollectReferencedSymbols(loaded, resolver);
        var (items, counts) = FindUnusedTypesWithFilterCounts(resolver, referencedSymbols, project, includeInternal);
        return (items, counts);
    }

    private static (List<UnusedSymbolInfo> items, Dictionary<string, int> counts)
        FindUnusedTypesWithFilterCounts(
            SymbolResolver resolver, HashSet<ISymbol> referencedSymbols, string? project, bool includeInternal)
    {
        var results = new List<UnusedSymbolInfo>();
        var counts = NewCounts();

        foreach (var type in resolver.AllTypes)
        {
            if (!type.Locations.Any(l => l.IsInSource)) continue;

            var projectName = resolver.GetProjectName(type);
            if (project != null && !projectName.Equals(project, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ShouldSkipType(type, includeInternal)) continue;

            var (typeFile, typeLine) = resolver.GetFileAndLine(type);
            var markupSource = resolver.GetMarkupSource(typeFile);

            // Razor infrastructure (_Imports.razor, _ViewImports/_ViewStart.cshtml) compiles to a
            // type nothing ever names, so it looks unused by construction — but deleting the file
            // strips every component's using directives and breaks the build. Reporting it is
            // worse than silent: it is confidently actionable and wrong.
            if (IsMarkupInfrastructure(markupSource))
                continue;

            var typeReason = DeadCodeFilters.Classify(type);
            if (typeReason != DeadCodeFilters.Reason.None)
            {
                counts[KeyFor(typeReason)]++;
                continue;
            }

            if (!referencedSymbols.Contains(type))
            {
                results.Add(new UnusedSymbolInfo(
                    type.ToDisplayString(), type.TypeKind.ToString(),
                    typeFile, typeLine, projectName, resolver.IsGenerated(typeFile),
                    markupSource));
                continue;
            }

            CollectUnusedMembers(type, referencedSymbols, includeInternal, projectName, resolver, results, counts);
        }

        return (results, counts);
    }

    private static void CollectUnusedMembers(
        INamedTypeSymbol type, HashSet<ISymbol> referencedSymbols, bool includeInternal,
        string projectName, SymbolResolver resolver, List<UnusedSymbolInfo> results,
        Dictionary<string, int> counts)
    {
        var interfaceImplementations = CollectInterfaceImplementations(type, resolver.SymbolComparer);

        foreach (var member in type.GetMembers())
        {
            if (ShouldSkipMember(member, includeInternal, interfaceImplementations)) continue;

            var memberReason = DeadCodeFilters.Classify(member);
            if (memberReason != DeadCodeFilters.Reason.None)
            {
                counts[KeyFor(memberReason)]++;
                continue;
            }

            if (!referencedSymbols.Contains(member))
            {
                var (file, line) = resolver.GetFileAndLine(member);
                var kind = member switch
                {
                    IMethodSymbol => "Method",
                    IPropertySymbol => "Property",
                    IFieldSymbol => "Field",
                    IEventSymbol => "Event",
                    _ => member.Kind.ToString(),
                };
                var memberSymbol = $"{type.ToDisplayString()}.{member.Name}";
                results.Add(new UnusedSymbolInfo(
                    memberSymbol, kind, file, line, projectName, resolver.IsGenerated(file),
                    resolver.GetMarkupSource(file)));
            }
        }
    }

    private static Dictionary<string, int> NewCounts() => new(StringComparer.Ordinal)
    {
        ["testMethod"] = 0,
        ["testContainer"] = 0,
        ["mcpTool"] = 0,
        ["generated"] = 0,
        ["composition"] = 0,
        ["interop"] = 0,
    };

    private static string KeyFor(DeadCodeFilters.Reason reason) => reason switch
    {
        DeadCodeFilters.Reason.TestMethod => "testMethod",
        DeadCodeFilters.Reason.TestContainer => "testContainer",
        DeadCodeFilters.Reason.McpTool => "mcpTool",
        DeadCodeFilters.Reason.Generated => "generated",
        DeadCodeFilters.Reason.Composition => "composition",
        DeadCodeFilters.Reason.Interop => "interop",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>
    /// Collects every symbol referenced anywhere in the solution.
    ///
    /// A generated document routinely references the very symbols it declares — the Razor
    /// generator emits a component's render tree into the same file that declares the component —
    /// and counting that as usage makes a component with no real consumers look used. That is why
    /// unused-component detection was previously arbitrary: MainLayout had zero references yet
    /// went unreported, while _Imports, whose generated file happens not to name it, was reported.
    /// Self-references inside a generated file are therefore not counted.
    ///
    /// The check is per symbol, not per file: a member declared in hand-written code-behind and
    /// genuinely called by the generated render tree stays used.
    ///
    /// Scoped to generated files on purpose. A component whose hand-written code-behind touches
    /// its own members still counts as used, because that is how this tool treats every
    /// hand-written type — suppressing self-reference there would change dead-code semantics for
    /// all C#, not just Razor, which is a far wider change than this fix warrants.
    /// </summary>
    private static HashSet<ISymbol> CollectReferencedSymbols(LoadedSolution loaded, SymbolResolver resolver)
    {
        var referencedSymbols = new HashSet<ISymbol>(resolver.SymbolComparer);

        foreach (var (_, compilation) in loaded.Compilations)
        {
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();
                var treePath = syntaxTree.FilePath;
                var suppressSelfReferences = resolver.IsGenerated(treePath);

                foreach (var node in root.DescendantNodes())
                {
                    ISymbol? symbol = node switch
                    {
                        IdentifierNameSyntax identifier => semanticModel.GetSymbolInfo(identifier).Symbol,
                        GenericNameSyntax genericName => semanticModel.GetSymbolInfo(genericName).Symbol,
                        ObjectCreationExpressionSyntax creation => semanticModel.GetSymbolInfo(creation).Symbol,
                        _ => null
                    };

                    if (symbol == null)
                        continue;

                    if (!(suppressSelfReferences && DeclaredInFile(symbol, treePath)))
                    {
                        referencedSymbols.Add(symbol);
                        if (symbol.OriginalDefinition != null)
                            referencedSymbols.Add(symbol.OriginalDefinition);
                    }

                    if (symbol.ContainingType != null
                        && !(suppressSelfReferences && DeclaredInFile(symbol.ContainingType, treePath)))
                    {
                        referencedSymbols.Add(symbol.ContainingType);
                        if (symbol.ContainingType.OriginalDefinition != null)
                            referencedSymbols.Add(symbol.ContainingType.OriginalDefinition);
                    }
                }
            }
        }

        return referencedSymbols;
    }

    /// <summary>True when one of the symbol's own declarations lives in <paramref name="path"/>.</summary>
    private static bool DeclaredInFile(ISymbol symbol, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource &&
                string.Equals(location.SourceTree?.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] MarkupInfrastructureNames =
        ["_Imports.razor", "_ViewImports.cshtml", "_ViewStart.cshtml"];

    private static bool IsMarkupInfrastructure(string? markupPath) =>
        markupPath is not null &&
        MarkupInfrastructureNames.Any(n => Path.GetFileName(markupPath).Equals(n, StringComparison.OrdinalIgnoreCase));

    private static bool ShouldSkipType(INamedTypeSymbol type, bool includeInternal)
    {
        // Skip private or protected
        if (type.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
            return true;

        // Skip internal unless includeInternal
        if (!includeInternal && type.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal)
            return true;

        // Skip interfaces
        if (type.TypeKind == TypeKind.Interface)
            return true;

        // Skip static classes with extension methods (likely DI setup)
        if (type.IsStatic)
        {
            var members = type.GetMembers();
            var hasExtensionMethods = members
                .OfType<IMethodSymbol>()
                .Any(m => m.IsExtensionMethod);
            if (hasExtensionMethods)
                return true;
        }

        // Skip types containing a "Main" method (entry points)
        var mainMembers = type.GetMembers("Main");
        if (mainMembers.Length > 0)
            return true;

        return false;
    }

    private static bool ShouldSkipMember(
        ISymbol member, bool includeInternal, HashSet<ISymbol>? interfaceImplementations)
    {
        // Skip implicitly declared
        if (member.IsImplicitlyDeclared)
            return true;

        // Skip private or protected
        if (member.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
            return true;

        // Skip internal unless includeInternal
        if (!includeInternal && member.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal)
            return true;

        // Skip non-ordinary methods (property accessors, event accessors, constructors, etc.)
        if (member is IMethodSymbol method)
        {
            if (method.MethodKind != MethodKind.Ordinary)
                return true;

            // Skip override methods
            if (method.IsOverride)
                return true;
        }

        // Skip override properties
        if (member is IPropertySymbol { IsOverride: true })
            return true;

        // Skip implicit interface implementations: call sites bind to the interface member, so the
        // implementation itself is never referenced. Explicit implementations are private and skipped above.
        if (interfaceImplementations is not null && interfaceImplementations.Contains(member))
            return true;

        return false;
    }

    /// <summary>
    /// The members of <paramref name="type"/> that implement a member of one of its interfaces, or
    /// <see langword="null"/> when it implements none. FindImplementationForInterfaceMember takes the
    /// interface member, not the implementing one, so the map is built from the interface side once per type.
    /// </summary>
    private static HashSet<ISymbol>? CollectInterfaceImplementations(INamedTypeSymbol type, SymbolSignatureComparer comparer)
    {
        if (type.AllInterfaces.IsEmpty)
            return null;

        var implementations = new HashSet<ISymbol>(comparer);
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var interfaceMember in iface.GetMembers())
            {
                var impl = type.FindImplementationForInterfaceMember(interfaceMember);
                if (impl != null)
                    implementations.Add(impl);
            }
        }

        return implementations;
    }
}
