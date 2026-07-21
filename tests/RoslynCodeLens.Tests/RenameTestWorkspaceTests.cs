using Microsoft.CodeAnalysis;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests;

public class RenameTestWorkspaceTests
{
    [Fact]
    public void Create_ResolvesTypeFromSource()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Widget.cs", "namespace Demo; public class Widget { }"));

        Assert.False(loaded.IsEmpty);
        var symbols = resolver.FindSymbols("Widget");
        Assert.Single(symbols);
        Assert.Equal("Demo.Widget", symbols[0].ToDisplayString());
    }

    // The two opt-in capabilities below are silently wrong when absent:
    //  - the corelib-only default closure makes BCL extension methods unresolvable, so a LINQ
    //    assertion fails with the tool under test blameless;
    //  - without LanguageVersion.Preview a C# 14 `extension` block is a syntax error, so a test
    //    asserting "nothing found" passes for entirely the wrong reason.

    private const string LinqSource = """
        using System.Collections.Generic;
        using System.Linq;

        namespace Demo;

        public static class Consumer
        {
            public static IEnumerable<int> Filter() => new List<int>().Where(x => true);
        }
        """;

    private const string ExtensionBlockSource = """
        namespace Demo;

        public static class IntExtensions
        {
            extension(int value)
            {
                public int Tripled => value * 3;
            }
        }
        """;

    [Fact]
    public void FrameworkReferences_ResolveBclAndLinq()
    {
        var (loaded, _) = RenameTestWorkspace.Create(
            RenameTestWorkspaceOptions.FrameworkReferences,
            ("Consumer.cs", LinqSource));

        Assert.Empty(Errors(loaded));
    }

    [Fact]
    public void PreviewLanguageVersion_ParsesCSharp14ExtensionBlocks()
    {
        var (loaded, _) = RenameTestWorkspace.Create(
            RenameTestWorkspaceOptions.PreviewLanguage,
            ("IntExtensions.cs", ExtensionBlockSource));

        Assert.Empty(Errors(loaded));
    }

    private static IReadOnlyList<string> Errors(LoadedSolution loaded)
        => loaded.Compilations.Values
            .SelectMany(c => c.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
}
