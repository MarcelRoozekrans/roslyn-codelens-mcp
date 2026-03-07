using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

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
        var publicMembers = target.GetMembers()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public
                        && !m.IsImplicitlyDeclared
                        && m is not IMethodSymbol { MethodKind: MethodKind.Constructor })
            .Select(m => m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .ToList();

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

[McpServerToolType]
public static class GetSymbolContextTool
{
    [McpServerTool(Name = "get_symbol_context"),
     Description("One-shot context dump for a type: namespace, base class, interfaces, injected dependencies, public members")]
    public static SymbolContext? Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Type name (simple or fully qualified)")] string symbol)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return GetSymbolContextLogic.Execute(loaded, resolver, symbol);
    }
}
