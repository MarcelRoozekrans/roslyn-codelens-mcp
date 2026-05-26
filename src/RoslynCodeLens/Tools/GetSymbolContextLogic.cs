using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetSymbolContextLogic
{
    public static SymbolContext Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string symbol)
    {
        var types = resolver.FindNamedTypes(symbol);
        var target = types.FirstOrDefault(t => t.Locations.Any(l => l.IsInSource));
        if (target != null)
            return BuildContext(resolver, target, MetadataSymbolResolver.SourceOrigin);

        // No source-defined type with this name — try the metadata fallback.
        var resolved = metadata.Resolve(symbol);
        if (resolved?.Symbol is INamedTypeSymbol metadataType)
            return BuildMetadataContext(metadataType, resolved.Origin);

        throw new McpToolException(ToolErrorCode.SymbolNotFound, $"Symbol '{symbol}' not found.", new { symbol });
    }

    private static SymbolContext BuildContext(SymbolResolver resolver, INamedTypeSymbol target, SymbolOrigin origin)
    {
        var (file, line) = resolver.GetFileAndLine(target);
        var project = resolver.GetProjectName(target);

        // Base class (skip System.Object)
        string? baseClass = target.BaseType is { SpecialType: not SpecialType.System_Object }
            ? target.BaseType.ToDisplayString()
            : null;

        // Interfaces
        var interfaces = target.AllInterfaces
            .Select(i => i.ToDisplayString())
            .ToList();

        // Injected dependencies: constructor parameters
        var injectedDependencies = target.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Constructor && !m.IsImplicitlyDeclared)
            .SelectMany(ctor => ctor.Parameters)
            .Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}")
            .ToList();

        // Public members (skip constructors and implicit members)
        var publicMembers = BuildPublicMembers(target);

        return new SymbolContext(
            target.ToDisplayString(),
            target.ContainingNamespace.ToDisplayString(),
            project,
            file,
            line,
            baseClass,
            interfaces,
            injectedDependencies,
            publicMembers,
            Origin: origin);
    }

    private static SymbolContext BuildMetadataContext(INamedTypeSymbol target, SymbolOrigin origin)
    {
        string? baseClass = target.BaseType is { SpecialType: not SpecialType.System_Object }
            ? target.BaseType.ToDisplayString()
            : null;

        var interfaces = target.AllInterfaces
            .Select(i => i.ToDisplayString())
            .ToList();

        // Metadata types have no meaningful DI story — constructors on a referenced
        // interface/type aren't "dependencies injected into OUR code". Leave empty.
        // For metadata interfaces, include inherited members too: consumers calling
        // get_symbol_context on IServiceCollection expect to see Add/Remove/etc even
        // though those live on the base IList<T> interface.
        var publicMembers = BuildPublicMembers(target);
        if (target.TypeKind == TypeKind.Interface)
        {
            foreach (var iface in target.AllInterfaces)
            {
                publicMembers.AddRange(BuildPublicMembers(iface));
            }
        }

        return new SymbolContext(
            target.ToDisplayString(),
            target.ContainingNamespace.ToDisplayString(),
            Project: "",
            File: "",
            Line: 0,
            baseClass,
            interfaces,
            InjectedDependencies: [],
            publicMembers,
            Origin: origin);
    }

    private static List<string> BuildPublicMembers(INamedTypeSymbol target)
    {
        var publicMembers = new List<string>();
        foreach (var m in target.GetMembers())
        {
            if (m.DeclaredAccessibility == Accessibility.Public
                && !m.IsImplicitlyDeclared
                && m is not IMethodSymbol { MethodKind: MethodKind.Constructor })
            {
                publicMembers.Add(m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            }
        }
        return publicMembers;
    }
}
