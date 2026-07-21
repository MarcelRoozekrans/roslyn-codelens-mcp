using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

/// <summary>
/// The <c>taskSymbol is null</c> guard used to skip a WHOLE COMPILATION before any of its trees were
/// touched. Run per-tree instead — after the scanner's first-one-wins dedupe has already handed that
/// tree to the reference-less compilation — it makes the tree disappear from the healthy project too.
/// The guard belongs in the projectFilter, which is what the filter is for.
/// </summary>
public class FindAsyncViolationsMissingCorelibTests
{
    private const string Source = """
        namespace Demo;
        public class Worker
        {
            public System.Threading.Tasks.Task WorkAsync()
                => System.Threading.Tasks.Task.CompletedTask;

            public void Blocking() { WorkAsync().Wait(); }
        }
        """;

    [Fact]
    public void SharedFile_IsStillScanned_WhenTheFirstProjectLacksTask()
    {
        var (loaded, resolver) = UnreferencedProjectWorkspace.Create(@"C:\sln\Worker.cs", Source);

        var result = FindAsyncViolationsLogic.Execute(loaded, resolver);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(AsyncViolationPattern.SyncOverAsyncWait, violation.Pattern);
        Assert.Equal("ZzzHealthy", violation.Project);
    }
}
