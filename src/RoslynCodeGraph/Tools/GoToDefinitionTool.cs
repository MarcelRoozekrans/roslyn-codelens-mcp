using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GoToDefinitionLogic
{
    public static List<SymbolLocation> Execute(SymbolResolver resolver, string symbol)
    {
        var symbols = resolver.FindSymbols(symbol);
        if (symbols.Count == 0)
            return [];

        var results = new List<SymbolLocation>();
        var seen = new HashSet<string>();

        foreach (var s in symbols)
        {
            var (file, line) = resolver.GetFileAndLine(s);
            if (string.IsNullOrEmpty(file))
                continue;

            var fullName = s.ToDisplayString();
            if (!seen.Add(fullName))
                continue;

            var kind = s switch
            {
                INamedTypeSymbol t => t.TypeKind switch
                {
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Enum => "enum",
                    TypeKind.Delegate => "delegate",
                    _ => "class"
                },
                IMethodSymbol => "method",
                IPropertySymbol => "property",
                IFieldSymbol => "field",
                IEventSymbol => "event",
                _ => "symbol"
            };

            var project = resolver.GetProjectName(s);
            results.Add(new SymbolLocation(kind, fullName, file, line, project, resolver.IsGenerated(file)));
        }

        return results;
    }
}

[McpServerToolType]
public static class GoToDefinitionTool
{
    [McpServerTool(Name = "go_to_definition"),
     Description("Find the source file and line where a symbol is defined")]
    public static List<SymbolLocation> Execute(
        SolutionManager manager,
        [Description("Symbol name: simple type (MyClass), fully qualified (Namespace.MyClass), or member (MyClass.DoWork)")] string symbol)
    {
        manager.EnsureLoaded();
        return GoToDefinitionLogic.Execute(manager.GetResolver(), symbol);
    }
}
