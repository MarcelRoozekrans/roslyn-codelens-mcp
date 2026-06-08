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
        var referencedSymbols = CollectReferencedSymbols(loaded);
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

            var typeReason = DeadCodeFilters.Classify(type);
            if (typeReason != DeadCodeFilters.Reason.None)
            {
                counts[KeyFor(typeReason)]++;
                continue;
            }

            if (!referencedSymbols.Contains(type))
            {
                var (file, line) = resolver.GetFileAndLine(type);
                results.Add(new UnusedSymbolInfo(
                    type.ToDisplayString(), type.TypeKind.ToString(),
                    file, line, projectName, resolver.IsGenerated(file)));
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
        foreach (var member in type.GetMembers())
        {
            if (ShouldSkipMember(member, type, includeInternal)) continue;

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
                    memberSymbol, kind, file, line, projectName, resolver.IsGenerated(file)));
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

    private static HashSet<ISymbol> CollectReferencedSymbols(LoadedSolution loaded)
    {
        var referencedSymbols = new HashSet<ISymbol>(SymbolSignatureComparer.Instance);

        foreach (var (_, compilation) in loaded.Compilations)
        {
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

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

                    referencedSymbols.Add(symbol);
                    if (symbol.OriginalDefinition != null)
                        referencedSymbols.Add(symbol.OriginalDefinition);

                    if (symbol.ContainingType != null)
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

    private static bool ShouldSkipMember(ISymbol member, INamedTypeSymbol containingType, bool includeInternal)
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

            // Skip interface implementation members
            foreach (var iface in containingType.AllInterfaces)
            {
                var impl = containingType.FindImplementationForInterfaceMember(method);
                if (impl != null && SymbolSignatureComparer.Instance.Equals(impl, method))
                    return true;
            }
        }

        // Skip override properties
        if (member is IPropertySymbol prop)
        {
            if (prop.IsOverride)
                return true;

            // Skip interface implementation properties
            foreach (var iface in containingType.AllInterfaces)
            {
                var impl = containingType.FindImplementationForInterfaceMember(prop);
                if (impl != null && SymbolSignatureComparer.Instance.Equals(impl, prop))
                    return true;
            }
        }

        return false;
    }
}
