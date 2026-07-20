using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Preview-only, so the checked-in TestSolution files stay pristine. `Greeter.Greet` really is
/// `string Greet(string name)` — one parameter, virtual, implementing `IGreeter` and overridden by
/// `FancyGreeter` — so `add` (not `reorder`) is the operation that exercises call-site rewriting
/// across projects here.
/// </summary>
[Collection("TestSolution")]
public class ChangeSignatureFixtureTests
{
    private readonly TestSolutionFixture _fixture;

    public ChangeSignatureFixtureTests(TestSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PreviewAddParameterToGreet_EditsDeclarationAndCallSitesAcrossProjects()
    {
        var result = await ChangeSignatureLogic.ExecuteAsync(
            _fixture.Loaded, _fixture.Resolver, "TestLib.Greeter.Greet",
            [new SignatureOperation("add",
                Name: "salutation", Type: "System.String", CallSiteValue: "\"Hello\"")],
            preview: true, force: false, commitToMemory: null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(result.Applied);
        Assert.Equal("Greet(string name)", result.OldSignature);
        Assert.Equal("Greet(string name, string salutation)", result.NewSignature);

        // The declaration itself.
        Assert.Contains(result.Edits, e =>
            string.Equals(Path.GetFileName(e.FilePath), "Greeter.cs", StringComparison.OrdinalIgnoreCase));

        // Call sites live in the xUnit/NUnit/MSTest fixture projects, so edits span > 1 project.
        var projects = result.Edits
            .Select(e => Path.GetFileName(Path.GetDirectoryName(e.FilePath)!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.True(projects.Count > 1,
            $"Expected edits across multiple projects, got: {string.Join(", ", projects)}");

        // Every existing call site receives the supplied call-site value.
        Assert.Contains(result.Edits, e => e.NewText.Contains("\"Hello\"", StringComparison.Ordinal));

        // FancyGreeter overrides Greet, so the cascade must name it.
        Assert.Contains(result.CascadedTo, s => s.Contains("FancyGreeter.Greet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewLeavesFixtureFilesPristine()
    {
        var greeterPath = _fixture.Resolver.FindMethods("TestLib.Greeter.Greet")
            .Select(m => m.Locations.First(l => l.IsInSource).SourceTree!.FilePath)
            .First();
        var before = await File.ReadAllTextAsync(greeterPath);

        var result = await ChangeSignatureLogic.ExecuteAsync(
            _fixture.Loaded, _fixture.Resolver, "TestLib.Greeter.Greet",
            [new SignatureOperation("add",
                Name: "salutation", Type: "System.String", CallSiteValue: "\"Hello\"")],
            preview: true, force: false, commitToMemory: null, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(before, await File.ReadAllTextAsync(greeterPath));
    }
}
