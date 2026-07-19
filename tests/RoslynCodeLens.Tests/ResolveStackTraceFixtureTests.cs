using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

[Collection("TestSolution")]
public class ResolveStackTraceFixtureTests
{
    private readonly TestSolutionFixture _fixture;
    public ResolveStackTraceFixtureTests(TestSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public void RealisticTrace_ResolvesSourceAndMetadataFrames_InOrder()
    {
        var frames = ResolveStackTraceLogic.Execute(
            _fixture.Loaded, _fixture.Resolver, _fixture.Metadata, """
            System.InvalidOperationException: boom
               at System.String.Concat(String str0, String str1)
               at TestLib.Greeter.Greet(String name)
               --- End of stack trace from previous location ---
            random log noise
            """);

        Assert.Equal(3, frames.Count);   // header + 2 frames; separator + noise dropped
        Assert.Equal("exception", frames[0].Kind);
        Assert.Equal("metadata", frames[1].Origin);
        var greet = frames[2];
        Assert.Equal("source", greet.Origin);
        Assert.EndsWith("Greeter.cs", greet.File!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TestLib", greet.Project);
    }
}
