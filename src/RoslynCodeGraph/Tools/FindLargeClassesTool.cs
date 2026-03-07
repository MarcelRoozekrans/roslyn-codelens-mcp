using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindLargeClassesLogic
{
    public static List<LargeClassInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, int maxMembers, int maxLines)
    {
        var results = new List<LargeClassInfo>();

        foreach (var type in resolver.AllTypes)
        {
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
                continue;

            if (!type.Locations.Any(l => l.IsInSource))
                continue;

            var projectName = resolver.GetProjectName(type);

            if (project != null &&
                !projectName.Equals(project, StringComparison.OrdinalIgnoreCase))
                continue;

            var memberCount = type.GetMembers().Count(m => !m.IsImplicitlyDeclared);
            var lineCount = GetLineCount(type);

            if (memberCount >= maxMembers || lineCount >= maxLines)
            {
                var (file, line) = resolver.GetFileAndLine(type);
                results.Add(new LargeClassInfo(type.ToDisplayString(), memberCount, lineCount, file, line, projectName));
            }
        }

        return results.OrderByDescending(r => r.MemberCount).ToList();
    }

    private static int GetLineCount(INamedTypeSymbol type)
    {
        var syntaxRef = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return 0;
        var span = syntaxRef.Span;
        var tree = syntaxRef.SyntaxTree;
        var startLine = tree.GetLineSpan(span).StartLinePosition.Line;
        var endLine = tree.GetLineSpan(span).EndLinePosition.Line;
        return endLine - startLine + 1;
    }
}

[McpServerToolType]
public static class FindLargeClassesTool
{
    [McpServerTool(Name = "find_large_classes"),
     Description("Find classes and structs that exceed member count or line count thresholds")]
    public static List<LargeClassInfo> Execute(
        SolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Maximum members before flagging (default: 20)")] int maxMembers = 20,
        [Description("Maximum lines before flagging (default: 500)")] int maxLines = 500)
    {
        manager.EnsureLoaded();
        return FindLargeClassesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project, maxMembers, maxLines);
    }
}
