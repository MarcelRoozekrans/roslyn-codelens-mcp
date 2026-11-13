using RoslynCodeLens.Analysis;
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
        Assert.Equal(expected, GeneratorNames.InferFromPath(filePath, hintName));
    }

    [Theory]
    [InlineData(null, "Some.g.cs")]
    [InlineData("C:/proj/Some.g.cs", "")]
    [InlineData("C:/completely-unrelated.cs", "Some.g.cs")]
    [InlineData("Some.g.cs", "Some.g.cs")]
    public void InferGeneratorName_FallsBackToUnknown_WhenPathShapeIsUnexpected(string? filePath, string hintName)
    {
        Assert.Equal(GetSourceGeneratorsLogic.UnknownGenerator,
            GeneratorNames.InferFromPath(filePath, hintName));
    }

    [Theory]
    [InlineData(
        @"C:/proj/obj/Debug/net10.0/System.Text.RegularExpressions.Generator/System.Text.RegularExpressions.Generator.RegexGenerator/RegexGenerator.g.cs",
        "System.Text.RegularExpressions.Generator.RegexGenerator")]
    [InlineData(
        @"C:\proj\obj\Debug\net10.0\Riok.Mapperly\Riok.Mapperly.MapperGenerator\Mappers.Mapper.g.cs",
        "Riok.Mapperly.MapperGenerator")]
    [InlineData(
        @"C:/proj/obj/Debug/net10.0/Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Components/Pages/Counter_razor.g.cs",
        "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator")]
    public void Resolve_MatchesTheGeneratorIdentity_NotAPathSegmentGuess(string filePath, string expected)
    {
        var identities = new[]
        {
            new GeneratorIdentity("System.Text.RegularExpressions.Generator", "System.Text.RegularExpressions.Generator.RegexGenerator"),
            new GeneratorIdentity("Riok.Mapperly", "Riok.Mapperly.MapperGenerator"),
            new GeneratorIdentity("Microsoft.CodeAnalysis.Razor.Compiler", "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator"),
        };

        Assert.Equal(expected, GeneratorNames.Resolve(identities, filePath, hintName: null));
    }

    [Fact]
    public void Resolve_FallsBackToHintNameInference_WhenNoIdentityMatches()
    {
        var name = GeneratorNames.Resolve(
            Array.Empty<GeneratorIdentity>(),
            "C:/proj/obj/Debug/net10.0/Some.Asm/Some.Asm.Gen/Components/X.g.cs",
            "Components/X.g.cs");

        Assert.Equal("Some.Asm.Gen", name);
    }

    [Fact]
    public void Resolve_StaysUnknown_ForAFileNoGeneratorProduced()
    {
        var identities = new[] { new GeneratorIdentity("Riok.Mapperly", "Riok.Mapperly.MapperGenerator") };

        Assert.Equal(GeneratorNames.Unknown,
            GeneratorNames.Resolve(identities, "C:/proj/obj/Debug/net10.0/Proj.AssemblyInfo.cs", hintName: null));
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
