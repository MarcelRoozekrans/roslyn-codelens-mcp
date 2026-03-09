using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class FindLargeClassesLogic
{
    public static IReadOnlyList<LargeClassInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, int maxMembers, int maxLines)
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

            var memberCount = 0;
            foreach (var m in type.GetMembers())
            {
                if (!m.IsImplicitlyDeclared) memberCount++;
            }
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
        var lineSpan = tree.GetLineSpan(span);
        var startLine = lineSpan.StartLinePosition.Line;
        var endLine = lineSpan.EndLinePosition.Line;
        return endLine - startLine + 1;
    }
}
