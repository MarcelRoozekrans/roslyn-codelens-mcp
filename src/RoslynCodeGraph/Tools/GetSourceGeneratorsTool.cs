using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetSourceGeneratorsLogic
{
    public static List<SourceGeneratorInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project)
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
                .GroupBy(f => InferGeneratorName(f))
                .ToList();

            foreach (var group in byGenerator)
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
            for (var i = objIndex + 3; i < parts.Length - 1; i++)
            {
                var segment = parts[i];
                if (!segment.Equals("generated", StringComparison.OrdinalIgnoreCase)
                    && !segment.Contains('.'))
                {
                    return segment;
                }
            }
        }

        return "Unknown";
    }
}

[McpServerToolType]
public static class GetSourceGeneratorsTool
{
    [McpServerTool(Name = "get_source_generators"),
     Description("List source generators and their output per project")]
    public static List<SourceGeneratorInfo> Execute(
        SolutionManager manager,
        [Description("Optional project name filter")] string? project = null)
    {
        manager.EnsureLoaded();
        return GetSourceGeneratorsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project);
    }
}
