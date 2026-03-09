using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetSourceGeneratorsLogic
{
    public static IReadOnlyList<SourceGeneratorInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project)
    {
        var results = new List<SourceGeneratorInfo>();

        foreach (var proj in loaded.Solution.Projects)
        {
            if (project != null && !proj.Name.Equals(project, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!loaded.Compilations.TryGetValue(proj.Id, out var compilation))
                continue;

            var byGenerator = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!resolver.IsGenerated(tree.FilePath))
                    continue;
                var genName = InferGeneratorName(tree.FilePath);
                if (!byGenerator.TryGetValue(genName, out var fileList))
                {
                    fileList = new List<string>();
                    byGenerator[genName] = fileList;
                }
                fileList.Add(tree.FilePath);
            }

            if (byGenerator.Count == 0)
                continue;

            foreach (var (genName, files) in byGenerator)
            {
                results.Add(new SourceGeneratorInfo(
                    genName,
                    proj.Name,
                    files.Count,
                    files));
            }
        }

        return results;
    }

    private static string InferGeneratorName(string filePath)
    {
        var parts = filePath.Replace('\\', '/').Split('/');
        var objIndex = Array.FindIndex(parts, p => p.Equals("obj", StringComparison.OrdinalIgnoreCase));

        if (objIndex >= 0 && objIndex + 3 < parts.Length)
        {
#pragma warning disable HLQ013
            for (var i = objIndex + 3; i < parts.Length - 1; i++)
#pragma warning restore HLQ013
            {
                var segment = parts[i];
                if (!segment.Equals("generated", StringComparison.OrdinalIgnoreCase)
                    && !segment.Contains('.', StringComparison.Ordinal))
                {
                    return segment;
                }
            }
        }

        return "Unknown";
    }
}
