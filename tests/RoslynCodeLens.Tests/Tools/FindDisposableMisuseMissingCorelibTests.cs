using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

/// <summary>
/// Twin of <see cref="FindAsyncViolationsMissingCorelibTests"/>: the
/// "neither IDisposable nor IAsyncDisposable resolves" guard skipped a whole compilation before the
/// migration and a single tree after it — by which point the tree's dedupe slot is already spent.
/// </summary>
public class FindDisposableMisuseMissingCorelibTests
{
    private const string Source = """
        namespace Demo;
        public class Resource : System.IDisposable
        {
            public void Dispose() { }
        }

        public class Consumer
        {
            public void Leak() { var r = new Resource(); }
        }
        """;

    [Fact]
    public void SharedFile_IsStillScanned_WhenTheFirstProjectLacksIDisposable()
    {
        var (loaded, resolver) = UnreferencedProjectWorkspace.Create(@"C:\sln\Resource.cs", Source);

        var result = FindDisposableMisuseLogic.Execute(loaded, resolver);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(DisposableMisusePattern.DisposableNotDisposed, violation.Pattern);
        Assert.Equal("ZzzHealthy", violation.Project);
    }
}
