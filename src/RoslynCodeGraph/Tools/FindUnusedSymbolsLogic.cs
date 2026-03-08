using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindUnusedSymbolsLogic
{
    public static IReadOnlyList<UnusedSymbolInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, bool includeInternal)
    {
        var referencedSymbols = CollectReferencedSymbols(loaded);
        return FindUnusedTypes(resolver, referencedSymbols, project, includeInternal);
    }

    private static HashSet<ISymbol> CollectReferencedSymbols(LoadedSolution loaded)
    {
        var referencedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

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

    private static List<UnusedSymbolInfo> FindUnusedTypes(
        SymbolResolver resolver, HashSet<ISymbol> referencedSymbols, string? project, bool includeInternal)
    {
        var results = new List<UnusedSymbolInfo>();

        foreach (var type in resolver.AllTypes)
        {
            if (!type.Locations.Any(l => l.IsInSource))
                continue;

            var projectName = resolver.GetProjectName(type);

            if (project != null &&
                !projectName.Equals(project, StringComparison.OrdinalIgnoreCase))
                continue;

            if (ShouldSkipType(type, includeInternal))
                continue;

            if (!referencedSymbols.Contains(type))
            {
                var (file, line) = resolver.GetFileAndLine(type);
                results.Add(new UnusedSymbolInfo(type.ToDisplayString(), type.TypeKind.ToString(), file, line, projectName, resolver.IsGenerated(file)));
                continue;
            }

            CollectUnusedMembers(type, referencedSymbols, includeInternal, projectName, resolver, results);
        }

        return results;
    }

    private static void CollectUnusedMembers(
        INamedTypeSymbol type, HashSet<ISymbol> referencedSymbols, bool includeInternal,
        string projectName, SymbolResolver resolver, List<UnusedSymbolInfo> results)
    {
        foreach (var member in type.GetMembers())
        {
            if (ShouldSkipMember(member, type, includeInternal))
                continue;

            if (!referencedSymbols.Contains(member))
            {
                var (file, line) = resolver.GetFileAndLine(member);
                var kind = member switch
                {
                    IMethodSymbol => "Method",
                    IPropertySymbol => "Property",
                    IFieldSymbol => "Field",
                    IEventSymbol => "Event",
                    _ => member.Kind.ToString()
                };
                var memberSymbol = $"{type.ToDisplayString()}.{member.Name}";
                results.Add(new UnusedSymbolInfo(memberSymbol, kind, file, line, projectName, resolver.IsGenerated(file)));
            }
        }
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
        if (mainMembers.Any())
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
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, method))
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
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, prop))
                    return true;
            }
        }

        return false;
    }
}
