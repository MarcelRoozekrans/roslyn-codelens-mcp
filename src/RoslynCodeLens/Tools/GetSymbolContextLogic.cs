using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetSymbolContextLogic
{
    public static SymbolContext? Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var types = resolver.FindNamedTypes(symbol);
        if (types.Count == 0)
            return null;

        var target = types[0];

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

        return new SymbolContext(
            target.ToDisplayString(),
            target.ContainingNamespace.ToDisplayString(),
            project,
            file,
            line,
            baseClass,
            interfaces,
            injectedDependencies,
            publicMembers);
    }
}
