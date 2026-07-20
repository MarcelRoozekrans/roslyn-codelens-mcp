using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class FindThrowSitesLogicTests
{
    private const string Source = """
        using System;
        using System.IO;

        namespace Demo;

        public class CustomException : InvalidOperationException
        {
        }

        public class Thrower
        {
            public void Direct() { throw new InvalidOperationException("boom"); }

            public void Derived() { throw new CustomException(); }

            public void Rethrow()
            {
                try { Read(); }
                catch (IOException) { throw; }
            }

            public void Read() { throw new IOException(); }

            public void InLambda()
            {
                Action a = () => throw new NotSupportedException();
                a();
            }

            public void Documented() { throw new ArgumentNullException("p"); }
        }
        """;

    private static IReadOnlyList<ThrowSiteInfo> Run(string exceptionType, bool includeDerived = false)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Thrower.cs", Source));
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        return FindThrowSitesLogic.Execute(loaded, resolver, metadata, exceptionType, includeDerived);
    }

    [Fact]
    public void ExactType_Found()
    {
        var sites = Run("System.InvalidOperationException");

        var site = Assert.Single(sites);
        Assert.Equal("System.InvalidOperationException", site.ExceptionType);
        Assert.Contains("Direct", site.Method, StringComparison.Ordinal);
        Assert.False(site.IsRethrow);
        Assert.Equal("RenameProj", site.Project);
        Assert.Contains("throw new InvalidOperationException", site.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void IncludeDerived_FindsSubclasses()
    {
        var sites = Run("System.InvalidOperationException", includeDerived: true);

        Assert.Equal(2, sites.Count);
        Assert.Contains(sites, s => s.ExceptionType == "System.InvalidOperationException");
        Assert.Contains(sites, s => s.ExceptionType == "Demo.CustomException");
    }

    [Fact]
    public void Rethrow_IsFlagged()
    {
        var sites = Run("System.IO.IOException");

        Assert.Equal(2, sites.Count);
        var rethrow = Assert.Single(sites, s => s.IsRethrow);
        Assert.Contains("Rethrow", rethrow.Method, StringComparison.Ordinal);
        Assert.Equal("System.IO.IOException", rethrow.ExceptionType);
    }

    [Fact]
    public void LambdaThrow_IsStillAThrowSite()
    {
        // This tool scans every throw in the source — a throw inside a lambda IS a throw site in
        // the file, so it surfaces, attributed to the member that lexically contains it. The flow
        // tool asks a different question ("what escapes this method?") and therefore does NOT
        // descend into lambdas. The two behaviours are deliberately different.
        var sites = Run("System.NotSupportedException");

        var site = Assert.Single(sites);
        Assert.Contains("InLambda", site.Method, StringComparison.Ordinal);
    }

    [Fact]
    public void NonExceptionType_Throws()
    {
        var error = Assert.Throws<McpToolException>(() => Run("System.String"));
        Assert.Equal(ToolErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public void UnknownType_Throws()
    {
        var error = Assert.Throws<McpToolException>(() => Run("Demo.NoSuchException"));
        Assert.Equal(ToolErrorCode.SymbolNotFound, error.Code);
    }

    [Fact]
    public void MetadataType_Resolves()
    {
        var sites = Run("System.ArgumentNullException");

        var site = Assert.Single(sites);
        Assert.Contains("Documented", site.Method, StringComparison.Ordinal);
        Assert.True(site.Line > 0);
        Assert.True(site.Column > 0);
    }
}
