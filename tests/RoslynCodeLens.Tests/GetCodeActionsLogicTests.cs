using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests;

[Collection("TestSolution")]
public class GetCodeActionsLogicTests
{
    private readonly LoadedSolution _loaded;

    public GetCodeActionsLogicTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
    }

    [Fact]
    public async Task ExecuteAsync_AtMethodPosition_ReturnsCodeActions()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var greeterPath = project.Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal)).FilePath!;

        var result = await GetCodeActionsLogic.ExecuteAsync(
            _loaded, greeterPath, line: 8, column: 5,
            endLine: null, endColumn: null, CancellationToken.None);

        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidFile_ReturnsEmpty()
    {
        var result = await GetCodeActionsLogic.ExecuteAsync(
            _loaded, "nonexistent.cs", line: 1, column: 1,
            endLine: null, endColumn: null, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteAsync_PreCancelledToken_ThrowsOperationCancelled()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var greeterPath = project.Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal)).FilePath!;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await GetCodeActionsLogic.ExecuteAsync(
                _loaded, greeterPath, line: 8, column: 5,
                endLine: null, endColumn: null, cts.Token));
    }
}
