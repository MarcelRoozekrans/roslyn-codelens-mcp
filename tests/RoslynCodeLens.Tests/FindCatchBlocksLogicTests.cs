using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class FindCatchBlocksLogicTests
{
    private const string Source = """
        using System;
        using System.IO;

        namespace Demo;

        public class Catcher
        {
            public void Exact() { try { Work(); } catch (IOException) { } }

            public void Base() { try { Work(); } catch (Exception) { Console.WriteLine("x"); } }

            public void Bare() { try { Work(); } catch { Console.WriteLine("x"); } }

            public void Filtered() { try { Work(); } catch (IOException) when (DateTime.Now.Year > 0) { Console.WriteLine("x"); } }

            public void Rethrowing() { try { Work(); } catch (IOException) { throw; } }

            public void Unrelated() { try { Work(); } catch (NotSupportedException) { } }

            public void Work() { throw new IOException(); }
        }
        """;

    private static IReadOnlyList<CatchBlockInfo> Run(string exceptionType, bool includeBaseClauses = false)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Catcher.cs", Source));
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        return FindCatchBlocksLogic.Execute(loaded, resolver, metadata, exceptionType, includeBaseClauses);
    }

    [Fact]
    public void ExactType_Found()
    {
        var blocks = Run("System.IO.IOException");

        // Exact, Filtered, Rethrowing — not Base, Bare or Unrelated.
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.Equal("System.IO.IOException", b.CaughtType));
        Assert.Contains(blocks, b => b.Method.Contains("Exact", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, b => b.Method.Contains("Base", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, b => b.Method.Contains("Bare", StringComparison.Ordinal));
        Assert.Equal("RenameProj", blocks[0].Project);
    }

    [Fact]
    public void IncludeBaseClauses_FindsBaseAndBare()
    {
        var blocks = Run("System.IO.IOException", includeBaseClauses: true);

        Assert.Equal(5, blocks.Count);
        Assert.Contains(blocks, b => b.Method.Contains("Base", StringComparison.Ordinal));
        Assert.Contains(blocks, b => b.Method.Contains("Bare", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, b => b.Method.Contains("Unrelated", StringComparison.Ordinal));
    }

    [Fact]
    public void Filter_IsFlagged()
    {
        var blocks = Run("System.IO.IOException");

        var filtered = Assert.Single(blocks, b => b.HasFilter);
        Assert.Contains("Filtered", filtered.Method, StringComparison.Ordinal);
        Assert.Contains("when", filtered.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCatch_IsSwallow()
    {
        var blocks = Run("System.IO.IOException");

        var exact = Assert.Single(blocks, b => b.Method.Contains("Exact", StringComparison.Ordinal));
        Assert.True(exact.IsEmpty);
        Assert.False(exact.Rethrows);
    }

    [Fact]
    public void RethrowingCatch_IsFlagged()
    {
        var blocks = Run("System.IO.IOException");

        var rethrowing = Assert.Single(blocks, b => b.Method.Contains("Rethrowing", StringComparison.Ordinal));
        Assert.True(rethrowing.Rethrows);
        Assert.False(rethrowing.IsEmpty);
    }

    [Fact]
    public void BareCatch_HasNullType()
    {
        var blocks = Run("System.IO.IOException", includeBaseClauses: true);

        var bare = Assert.Single(blocks, b => b.CaughtType == null);
        Assert.Contains("Bare", bare.Method, StringComparison.Ordinal);
        Assert.True(bare.Line > 0);
        Assert.True(bare.Column > 0);
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
    public void GeneratedTrees_AreSkipped()
    {
        // Same rule as every sibling solution-wide scan: a handler a generator emitted is not a
        // handler a developer wrote or can change.
        const string Generated = """
            using System;
            using System.IO;

            namespace Demo;

            public class GeneratedCatcher
            {
                public void Handle() { try { } catch (IOException) { } }
            }
            """;

        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Catcher.cs", Source), ("Generated.g.cs", Generated));
        var metadata = new MetadataSymbolResolver(loaded, resolver);

        var blocks = FindCatchBlocksLogic.Execute(
            loaded, resolver, metadata, "System.IO.IOException", includeBaseClauses: false);

        Assert.DoesNotContain(blocks, b => b.File.EndsWith(".g.cs", StringComparison.Ordinal));
    }
}
