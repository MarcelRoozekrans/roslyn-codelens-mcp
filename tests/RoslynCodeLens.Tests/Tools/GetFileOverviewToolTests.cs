using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetFileOverviewToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly string _greeterPath;

    public GetFileOverviewToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _greeterPath = _loaded.Solution.Projects
            .First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal))
            .Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal))
            .FilePath!;
    }

    [Fact]
    public async Task ExecuteAsync_ForGreeterFile_ReturnsOverview()
    {
        var result = await GetFileOverviewLogic.ExecuteAsync(
            _loaded, _resolver, _greeterPath, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.TypesDefined);
        Assert.Contains("Greeter", result.TypesDefined);
        Assert.NotNull(result.Project);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidFile_ThrowsFileNotFound()
    {
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            GetFileOverviewLogic.ExecuteAsync(
                _loaded, _resolver, "nonexistent.cs", CancellationToken.None));

        Assert.Equal(ToolErrorCode.FileNotFound, ex.Code);
    }

    // Issue #399: a .razor file is an AdditionalDocument, so it has no C# document of its own and
    // get_file_overview reported FileNotFound. It is resolved through to the source generator's
    // output instead, and the Razor generator names that output after the component's
    // project-relative path: Components/Pages/Counter.razor -> Components/Pages/Counter_razor.g.cs.
    [Theory]
    [InlineData("Counter.razor", new[] { "Components", "Pages" }, "Components/Pages/Counter_razor.g.cs")]
    [InlineData("App.razor", new[] { "Components" }, "Components/App_razor.g.cs")]
    [InlineData("Index.cshtml", new string[0], "Index_cshtml.g.cs")]
    [InlineData("Weather.Details.razor", new[] { "Pages" }, "Pages/Weather_Details_razor.g.cs")]
    public void ExpectedHintName_MirrorsProjectRelativePath(string name, string[] folders, string expected)
    {
        using var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
        var documentId = Microsoft.CodeAnalysis.DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Markup", "Markup", Microsoft.CodeAnalysis.LanguageNames.CSharp)
            .AddAdditionalDocument(documentId, name, "", folders);

        var additional = solution.GetAdditionalDocument(documentId)!;

        Assert.Equal(expected, GetFileOverviewLogic.ExpectedHintName(additional));
    }
}
