using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class SolutionLoaderTimeoutTests
{
    [Fact]
    public async Task RunWithTimeoutAsync_FastTask_ReturnsResult()
    {
        var result = await SolutionLoader.RunWithTimeoutAsync<string>(
            ct => Task.FromResult<string?>("ok"),
            timeoutSec: 1,
            outerCt: default);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task RunWithTimeoutAsync_SlowTask_ReturnsNull()
    {
        var result = await SolutionLoader.RunWithTimeoutAsync<string>(
            async ct => { await Task.Delay(2000, ct).ConfigureAwait(false); return "late"; },
            timeoutSec: 1,
            outerCt: default);
        Assert.Null(result);
    }

    [Fact]
    public async Task RunWithTimeoutAsync_UserCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await SolutionLoader.RunWithTimeoutAsync<string>(
                async ct => { await Task.Delay(5000, ct).ConfigureAwait(false); return "never"; },
                timeoutSec: 60,
                outerCt: cts.Token));
    }

    [Fact]
    public void GetOpenProjectTimeoutSec_DefaultsTo300()
    {
        Environment.SetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS", null);
        Assert.Equal(300, SolutionLoader.GetOpenProjectTimeoutSec());
    }

    [Fact]
    public void GetOpenProjectTimeoutSec_HonorsEnvVarOverride()
    {
        try
        {
            Environment.SetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS", "42");
            Assert.Equal(42, SolutionLoader.GetOpenProjectTimeoutSec());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS", null);
        }
    }
}
