using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetSourceGeneratorsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetSourceGeneratorsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public async Task Execute_ReturnsEmptyList_WhenNoGenerators()
    {
        var results = await GetSourceGeneratorsLogic.ExecuteAsync(_loaded, null);
        Assert.NotNull(results);
    }

    [Fact]
    public async Task Execute_FiltersByProject_WhenProjectSpecified()
    {
        var projectName = _loaded.Solution.Projects.First().Name;
        var results = await GetSourceGeneratorsLogic.ExecuteAsync(_loaded, projectName);
        Assert.NotNull(results);
        Assert.All(results, r => Assert.Equal(projectName, r.Project));
    }

    // Issue #399: the old implementation sniffed syntax-tree paths for an obj/ segment and named
    // the generator after the first dot-free path segment. For the Razor generator that produced
    // "Components" — a directory inside the hint name, not a generator.
    [Theory]
    [InlineData(
        @"C:/proj/obj/Debug/net10.0/Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Components/Pages/Counter_razor.g.cs",
        "Components/Pages/Counter_razor.g.cs",
        "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator")]
    [InlineData(
        @"C:/proj/obj/Debug/net10.0/Microsoft.AspNetCore.App.SourceGenerators/Microsoft.AspNetCore.SourceGenerators.PublicProgramSourceGenerator/PublicTopLevelProgram.Generated.g.cs",
        "PublicTopLevelProgram.Generated.g.cs",
        "Microsoft.AspNetCore.SourceGenerators.PublicProgramSourceGenerator")]
    public void InferGeneratorName_RecoversGeneratorTypeName(string filePath, string hintName, string expected)
    {
        Assert.Equal(expected, GetSourceGeneratorsLogic.InferGeneratorName(filePath, hintName));
    }

    [Theory]
    [InlineData(null, "Some.g.cs")]
    [InlineData("C:/proj/Some.g.cs", "")]
    [InlineData("C:/completely-unrelated.cs", "Some.g.cs")]
    [InlineData("Some.g.cs", "Some.g.cs")]
    public void InferGeneratorName_FallsBackToUnknown_WhenPathShapeIsUnexpected(string? filePath, string hintName)
    {
        Assert.Equal(GetSourceGeneratorsLogic.UnknownGenerator,
            GetSourceGeneratorsLogic.InferGeneratorName(filePath, hintName));
    }

    [Fact]
    public void Sort_OrdersByProjectThenGeneratorName()
    {
        var input = new List<SourceGeneratorInfo>
        {
            new("ZGen", "Foo", 0, Array.Empty<string>()),
            new("AGen", "Foo", 0, Array.Empty<string>()),
            new("MGen", "Bar", 0, Array.Empty<string>()),
        };

        var sorted = GetSourceGeneratorsTool.Sort(input);

        Assert.Collection(sorted,
            s => { Assert.Equal("Bar", s.Project); Assert.Equal("MGen", s.GeneratorName); },
            s => { Assert.Equal("Foo", s.Project); Assert.Equal("AGen", s.GeneratorName); },
            s => { Assert.Equal("Foo", s.Project); Assert.Equal("ZGen", s.GeneratorName); });
    }
}
