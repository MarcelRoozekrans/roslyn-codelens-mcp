using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetGeneratedCodeLogic
{
    public static List<GeneratedFileInfo> Execute(
        LoadedSolution loaded, SymbolResolver resolver, string? generator, string? file)
    {
        var results = new List<GeneratedFileInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!resolver.IsGenerated(tree.FilePath))
                    continue;

                if (file != null && !tree.FilePath.Contains(file, StringComparison.OrdinalIgnoreCase))
                    continue;

                var genName = InferGeneratorName(tree.FilePath);
                if (generator != null && !genName.Equals(generator, StringComparison.OrdinalIgnoreCase))
                    continue;

                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                var definedTypes = root.DescendantNodes()
                    .Where(n => n is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax)
                    .Select(n =>
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(n);
                        return symbol?.ToDisplayString() ?? "";
                    })
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                var sourceText = tree.GetText().ToString();

                results.Add(new GeneratedFileInfo(
                    tree.FilePath,
                    projectName,
                    genName,
                    definedTypes,
                    sourceText));
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
public static class GetGeneratedCodeTool
{
    [McpServerTool(Name = "get_generated_code"),
     Description("Inspect generated source code from source generators")]
    public static List<GeneratedFileInfo> Execute(
        SolutionManager manager,
        [Description("Generator name to filter by")] string? generator = null,
        [Description("File path (or partial match) to filter by")] string? file = null)
    {
        manager.EnsureLoaded();
        return GetGeneratedCodeLogic.Execute(
            manager.GetLoadedSolution(), manager.GetResolver(), generator, file);
    }
}
