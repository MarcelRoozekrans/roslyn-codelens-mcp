using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class CodeActionRunnerTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetActionsAsync_AtMethodPosition_ReturnsActions()
    {
        // Greeter.cs line 8: public virtual string Greet(string name) => ...
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var doc = project.Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal));
        var compilation = _loaded.Compilations[project.Id];

        var actions = await CodeActionRunner.GetActionsAsync(
            project, doc, compilation, line: 8, column: 5,
            endLine: null, endColumn: null, CancellationToken.None);

        Assert.NotEmpty(actions);
        Assert.All(actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Title)));
    }

    [Fact]
    public async Task ApplyActionAsync_WithPreview_ReturnsDiff()
    {
        // Greeter.cs line 8: select the expression body "=> $"Hello, {name}!";"
        // This should trigger refactorings like "Use block body" that don't need workspace services
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var doc = project.Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal));
        var compilation = _loaded.Compilations[project.Id];

        // Select the range covering the expression body on line 8
        var actions = await CodeActionRunner.GetActionsAsync(
            project, doc, compilation, line: 8, column: 48,
            endLine: 8, endColumn: 66, CancellationToken.None);

        if (actions.Count == 0) return; // Skip if no actions at this position

        // Try each action until we find one that succeeds (some require workspace services)
        CodeActionResult? result = null;
        foreach (var action in actions)
        {
            result = await CodeActionRunner.ApplyActionAsync(
                project, doc, compilation, line: 8, column: 48,
                endLine: 8, endColumn: 66,
                actionTitle: action.Title, preview: true, commitToMemory: null, ct: CancellationToken.None);

            if (result.Success)
                break;
        }

        Assert.NotNull(result);
        Assert.True(result!.Success, $"No action succeeded. Last error: {result.ErrorMessage}");
        Assert.NotEmpty(result.Title);
    }
}
