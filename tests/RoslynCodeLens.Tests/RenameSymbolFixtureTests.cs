using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

[Collection("TestSolution")]
public class RenameSymbolFixtureTests
{
    private readonly TestSolutionFixture _fixture;

    public RenameSymbolFixtureTests(TestSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PreviewRenameGreeter_ProducesEditsAcrossProjects()
    {
        var result = await RenameSymbolLogic.ExecuteAsync(
            _fixture.Loaded, _fixture.Resolver, "Greeter", "Salutations",
            renameOverloads: true, renameInStrings: false, renameInComments: true,
            preview: true, force: false, commitToMemory: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Applied);
        Assert.Empty(result.Conflicts);
        // Greeter is defined in TestLib and called from the xUnit/NUnit/MSTest fixture
        // projects (see TestSolutionFixture health probe), so edits must span >1 project.
        var projects = result.Edits
            .Select(e => Path.GetFileName(Path.GetDirectoryName(e.FilePath)!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.True(projects.Count > 1,
            $"Expected edits across multiple projects, got: {string.Join(", ", projects)}");
    }
}
