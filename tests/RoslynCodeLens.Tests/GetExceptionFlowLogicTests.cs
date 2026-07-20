using System.Text.Json;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class GetExceptionFlowLogicTests
{
    private const string Source = """
        using System;

        namespace Demo;

        public class Svc
        {
            public void Top() { Middle(); }

            public void Guarded() { try { Middle(); } catch (InvalidOperationException) { } }

            public void GuardedFiltered() { try { Middle(); } catch (InvalidOperationException) when (DateTime.Now.Year > 0) { } }

            public void Middle() { Deep(); }

            public void Deep() { throw new InvalidOperationException("boom"); }

            public void Direct() { throw new ArgumentNullException(); }

            public void CaughtLocally() { try { throw new ArgumentNullException(); } catch (ArgumentNullException) { } }

            public void Recursive() { Recursive(); throw new NotSupportedException(); }
        }
        """;

    private static ExceptionFlowResult Run(
        string method, int maxDepth = 3, bool includeDocumented = true, int maxNodes = 500)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", Source));
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        return GetExceptionFlowLogic.Execute(
            loaded, resolver, metadata, method, maxDepth, includeDocumented, maxNodes);
    }

    [Fact]
    public void DirectThrow_Escapes()
    {
        var result = Run("Svc.Direct");

        var item = Assert.Single(result.Exceptions);
        Assert.Equal("System.ArgumentNullException", item.ExceptionType);
        Assert.Equal("thrown", item.Origin);
        Assert.True(item.Escapes);
        Assert.Equal(0, item.Depth);
        Assert.Null(item.CaughtIn);
        Assert.False(item.HasFilter);
        Assert.Equal(["Demo.Svc.Direct()"], item.Path);
    }

    [Fact]
    public void CaughtLocally_DoesNotEscape()
    {
        var result = Run("Svc.CaughtLocally");

        var item = Assert.Single(result.Exceptions);
        Assert.False(item.Escapes);
        Assert.Contains("CaughtLocally", item.CaughtIn!, StringComparison.Ordinal);
        Assert.True(item.CaughtLine > 0);
        Assert.NotNull(item.CaughtFile);
    }

    [Fact]
    public void TransitiveThrow_ReachesRoot()
    {
        var result = Run("Svc.Top", maxDepth: 3);

        var item = Assert.Single(result.Exceptions);
        Assert.Equal("System.InvalidOperationException", item.ExceptionType);
        Assert.True(item.Escapes);
        Assert.Equal(2, item.Depth);
        Assert.Equal("Demo.Svc.Deep()", item.RaisedIn);
        Assert.Equal(["Demo.Svc.Top()", "Demo.Svc.Middle()", "Demo.Svc.Deep()"], item.Path);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void DepthLimit_Truncates()
    {
        var result = Run("Svc.Top", maxDepth: 1);

        Assert.Empty(result.Exceptions);
        Assert.True(result.Truncated);
        Assert.Equal(1, result.MaxDepthRequested);
    }

    [Fact]
    public void CaughtMidChain_DoesNotEscape()
    {
        var result = Run("Svc.Guarded");

        var item = Assert.Single(result.Exceptions);
        Assert.Equal("System.InvalidOperationException", item.ExceptionType);
        Assert.False(item.Escapes);
        Assert.Equal("Demo.Svc.Guarded()", item.CaughtIn);
        Assert.False(item.HasFilter);
    }

    [Fact]
    public void FilteredCatch_StillEscapes()
    {
        var result = Run("Svc.GuardedFiltered");

        var item = Assert.Single(result.Exceptions);
        Assert.True(item.Escapes);
        Assert.True(item.HasFilter);
    }

    [Fact]
    public void Recursion_Terminates()
    {
        var result = Run("Svc.Recursive");

        var item = Assert.Single(result.Exceptions);
        Assert.Equal("System.NotSupportedException", item.ExceptionType);
        Assert.True(item.Escapes);
    }

    [Fact]
    public void UnknownMethod_Throws()
    {
        var error = Assert.Throws<McpToolException>(() => Run("Svc.NoSuchMethod"));
        Assert.Equal(ToolErrorCode.SymbolNotFound, error.Code);
    }

    [Fact]
    public void Summary_CountsEscapingAndCaught()
    {
        var escaping = JsonSerializer.Serialize(Run("Svc.Direct").Summary);
        Assert.Contains("\"escaping\":1", escaping, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"caught\":0", escaping, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.ArgumentNullException", escaping, StringComparison.Ordinal);

        var caught = JsonSerializer.Serialize(Run("Svc.CaughtLocally").Summary);
        Assert.Contains("\"escaping\":0", caught, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"caught\":1", caught, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The documented-exception path needs XML documentation on metadata symbols, which only
/// MSBuildWorkspace wires up — an AdhocWorkspace reference built with a bare
/// <c>MetadataReference.CreateFromFile</c> carries none, so
/// <c>GetDocumentationCommentXml()</c> comes back empty there.
/// </summary>
[Collection("TestSolution")]
public class GetExceptionFlowFixtureTests
{
    private readonly TestSolutionFixture _fixture;

    public GetExceptionFlowFixtureTests(TestSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public void DocumentedMetadataException_IsReported()
    {
        // CallGraphSamples.CallsExternal() is `string.Format("{0}", "x")`, and string.Format
        // documents ArgumentNullException / FormatException via <exception cref="..."> tags.
        var result = GetExceptionFlowLogic.Execute(
            _fixture.Loaded, _fixture.Resolver, _fixture.Metadata,
            "CallGraphSamples.CallsExternal", maxDepth: 3, includeDocumented: true, maxNodes: 500);

        Assert.Contains(result.Exceptions, e =>
            string.Equals(e.Origin, "documented", StringComparison.Ordinal));
    }

    [Fact]
    public void IncludeDocumentedFalse_SuppressesDocumentedExceptions()
    {
        var result = GetExceptionFlowLogic.Execute(
            _fixture.Loaded, _fixture.Resolver, _fixture.Metadata,
            "CallGraphSamples.CallsExternal", maxDepth: 3, includeDocumented: false, maxNodes: 500);

        Assert.DoesNotContain(result.Exceptions, e =>
            string.Equals(e.Origin, "documented", StringComparison.Ordinal));
    }
}
