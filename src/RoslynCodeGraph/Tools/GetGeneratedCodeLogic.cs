using Microsoft.CodeAnalysis;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetGeneratedCodeLogic
{
    public static IReadOnlyList<GeneratedFileInfo> Execute(
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
