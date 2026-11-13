using Microsoft.CodeAnalysis;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetGeneratedCodeLogic
{
    public static IReadOnlyList<GeneratedFileInfo> Execute(
        LoadedSolution loaded, SymbolResolver resolver, string? generator, string? file)
    {
        var results = new List<GeneratedFileInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);
            var project = loaded.Solution.GetProject(projectId);
            var identities = project is null ? Array.Empty<GeneratorIdentity>() : GeneratorNames.GetIdentities(project);
            var generatedBase = project is null ? null : GeneratorNames.GetGeneratedFilesBase(project);

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!resolver.IsGenerated(tree.FilePath))
                    continue;

                if (file != null && !tree.FilePath.Contains(file, StringComparison.OrdinalIgnoreCase))
                    continue;

                var genName = GeneratorNames.Resolve(identities, tree.FilePath, hintName: null, generatedBase);
                if (generator != null && !genName.Equals(generator, StringComparison.OrdinalIgnoreCase))
                    continue;

                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                var definedTypes = new List<string>();
                foreach (var n in root.DescendantNodes())
                {
                    if (n is not Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax)
                        continue;
                    var symbol = semanticModel.GetDeclaredSymbol(n);
                    var displayString = symbol?.ToDisplayString();
                    if (!string.IsNullOrEmpty(displayString))
                        definedTypes.Add(displayString);
                }

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
}
