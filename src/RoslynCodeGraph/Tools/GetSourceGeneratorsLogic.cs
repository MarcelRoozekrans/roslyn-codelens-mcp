using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

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

            var generatedFiles = compilation.SyntaxTrees
                .Where(t => resolver.IsGenerated(t.FilePath))
                .Select(t => t.FilePath)
                .ToList();

            if (generatedFiles.Count == 0)
                continue;

            var byGenerator = generatedFiles
                .GroupBy(f => InferGeneratorName(f), StringComparer.Ordinal)
                .ToList();

            foreach (var group in CollectionsMarshal.AsSpan(byGenerator))
            {
                results.Add(new SourceGeneratorInfo(
                    group.Key,
                    proj.Name,
                    group.Count(),
                    group.ToList()));
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
