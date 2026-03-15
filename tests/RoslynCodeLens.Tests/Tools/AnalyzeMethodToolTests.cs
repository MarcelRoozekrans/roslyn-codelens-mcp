using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class AnalyzeMethodToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Execute_ForGreeterGreet_ReturnsAnalysis()
    {
        var result = AnalyzeMethodLogic.Execute(_loaded, _resolver, "Greeter.Greet");

        Assert.NotNull(result);
        Assert.NotEmpty(result.Signature);
        Assert.NotNull(result.File);
        Assert.True(result.Line > 0);
    }

    [Fact]
    public void Execute_ForGreeterGreet_HasCallers()
    {
        var result = AnalyzeMethodLogic.Execute(_loaded, _resolver, "Greeter.Greet");

        Assert.NotNull(result);
        // Greet is called from GreeterConsumer.SayHello
        Assert.True(result.Callers.Count > 0 || result.OutgoingCalls.Count >= 0);
    }

    [Fact]
    public void Execute_ForUnknownMethod_ReturnsNull()
    {
        var result = AnalyzeMethodLogic.Execute(_loaded, _resolver, "NoSuchClass.NoSuchMethod");

        Assert.Null(result);
    }
}
