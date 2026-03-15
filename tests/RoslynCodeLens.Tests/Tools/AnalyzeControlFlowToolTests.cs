using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class AnalyzeControlFlowToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private string _diSetupPath = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _diSetupPath = _loaded.Solution.Projects
            .First(p => string.Equals(p.Name, "TestLib2", StringComparison.Ordinal))
            .Documents.First(d => string.Equals(d.Name, "DiSetup.cs", StringComparison.Ordinal))
            .FilePath!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_OnBlockBodyWithReturn_ReturnsSucceededFlowInfo()
    {
        // DiSetup.cs AddGreeting method body spans lines 10-11:
        //   line 10: services.AddScoped<IGreeter, Greeter>();
        //   line 11: return services;
        var result = await AnalyzeControlFlowLogic.ExecuteAsync(
            _loaded, _diSetupPath, startLine: 10, endLine: 11, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.True(result.StartPointIsReachable);
        // The block ends with a return, so the end point is not reachable after the last statement
        Assert.Contains(result.ReturnStatements, s => s.Contains("return services"));
    }

    [Fact]
    public async Task ExecuteAsync_OnSingleStatement_ReturnsReachableEndPoint()
    {
        // DiSetup.cs line 10: services.AddScoped<IGreeter, Greeter>();
        // This is not a return statement, so end point is reachable after this statement.
        var result = await AnalyzeControlFlowLogic.ExecuteAsync(
            _loaded, _diSetupPath, startLine: 10, endLine: 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.True(result.StartPointIsReachable);
        Assert.True(result.EndPointIsReachable);
        Assert.Empty(result.ReturnStatements);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidFile_ReturnsNull()
    {
        var result = await AnalyzeControlFlowLogic.ExecuteAsync(
            _loaded, "nonexistent.cs", startLine: 1, endLine: 1, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExecuteAsync_OutOfBoundsLines_ReturnsNull()
    {
        var result = await AnalyzeControlFlowLogic.ExecuteAsync(
            _loaded, _diSetupPath, startLine: 9999, endLine: 9999, CancellationToken.None);

        Assert.Null(result);
    }
}
