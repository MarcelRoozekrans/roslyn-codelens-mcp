using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Symbols;

public class LogicalMemberGroupsTests
{
    [Fact]
    public void DistinctDeclarations_PartsConnectedOnlyByALaterSymbol_ReturnsOneSymbol()
    {
        // Alpha compiles only the first linked part and Beta only the second. Gamma compiles both and is the only symbol
        // that connects them, so all three are one declaration even though Alpha and Beta share no part.
        const string first = "namespace Shared; public partial class Widget { }";
        const string second = "namespace Shared; public partial class Widget { public int Size; }";
        var (_, resolver) = RenameTestWorkspace.Create(
            ("Alpha", [("Shared/Widget.First.cs", first)]),
            ("Beta", [("Shared/Widget.Second.cs", second)]),
            ("Gamma", [("Shared/Widget.First.cs", first), ("Shared/Widget.Second.cs", second)]));
        var types = resolver.FindNamedTypes("Shared.Widget")
            .OrderBy(t => t.ContainingAssembly.Name, StringComparer.Ordinal)
            .ToList<ISymbol>();

        var distinct = LogicalMemberGroups.DistinctDeclarations(types, resolver);

        Assert.Equal(["Alpha", "Beta", "Gamma"], types.Select(t => t.ContainingAssembly.Name));
        Assert.Equal("Alpha", Assert.Single(distinct).ContainingAssembly.Name);
    }

    /// <summary>
    /// A generated document is no project document: the compilations of a multi-targeted project generate it once each,
    /// but another project can generate the same path and span without sharing the declaration.
    /// </summary>
    /// <param name="secondAssembly">The assembly name of the second compilation.</param>
    /// <param name="expected">The assemblies of the distinct declarations.</param>
    [Theory]
    [InlineData("Alpha", new[] { "Alpha" })]
    [InlineData("Beta", new[] { "Alpha", "Beta" })]
    public void DistinctDeclarations_GeneratedMemberInTwoCompilations_IsOneDeclarationPerAssembly(
        string secondAssembly, string[] expected)
    {
        var (resolver, members) = TwoCompilationsWithGeneratedMember("Alpha", secondAssembly);

        var distinct = LogicalMemberGroups.DistinctDeclarations(members, resolver);

        Assert.Equal(2, members.Count);
        Assert.Equal(expected, distinct.Select(m => m.ContainingAssembly.Name));
    }

    // Two independent projects with the same document, and a generated tree added to each compilation as a source
    // generator would add it: same assembly name for the target frameworks of one project, different names otherwise.
    // Returns the generated member of each compilation, in project order.
    private static (SymbolResolver Resolver, List<ISymbol> Members) TwoCompilationsWithGeneratedMember(
        string firstAssembly, string secondAssembly)
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();
        var members = new List<ISymbol>();
        foreach (var (assembly, index) in new[] { firstAssembly, secondAssembly }.Select((a, i) => (a, i)))
        {
            var id = ProjectId.CreateNewId();
            solution = solution
                .AddProject(ProjectInfo.Create(
                        id, VersionStamp.Create(), $"{assembly}{index}", assembly, LanguageNames.CSharp,
                        compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                    .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
                .AddDocument(
                    DocumentId.CreateNewId(id), "Helper.cs", "namespace Duplicate; public partial class Helper { }",
                    filePath: $"{assembly}/Helper.cs");
            compilations[id] = solution.GetProject(id)!.GetCompilationAsync().GetAwaiter().GetResult()!
                .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                    "namespace Duplicate; partial class Helper { public int Generated() => 1; }",
                    path: "Gen/Gen.Generator/Helper.g.cs"));
            members.Add(compilations[id].GetTypeByMetadataName("Duplicate.Helper")!.GetMembers("Generated").Single());
        }

        return (new SymbolResolver(new LoadedSolution { Solution = solution, Compilations = compilations }), members);
    }
}