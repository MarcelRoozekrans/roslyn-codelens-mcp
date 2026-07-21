using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindDisposableMisuseToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindDisposableMisuseToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Theory]
    [InlineData(DisposableMisusePattern.DisposableNotDisposed, "NotDisposedFromConstructor")]
    [InlineData(DisposableMisusePattern.DisposableNotDisposed, "NotDisposedFromFactory")]
    [InlineData(DisposableMisusePattern.DisposableDiscarded, "DiscardedConstructor")]
    [InlineData(DisposableMisusePattern.DisposableDiscarded, "DiscardedFactory")]
    public void DetectsExactlyOneViolationPerPositiveCase(DisposableMisusePattern pattern, string methodName)
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);

        var hits = result.Violations.Where(v =>
            v.Pattern == pattern &&
            v.ContainingMethod.EndsWith(methodName, StringComparison.Ordinal)).ToList();

        Assert.Single(hits);
    }

    [Fact]
    public void DoesNotFlag_UsingDeclaration()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("DisposedViaUsingDeclaration", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_UsingStatement()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("DisposedViaUsingStatement", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_AwaitUsing()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("DisposedViaAwaitUsing", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_Returned()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("ReturnedToCaller", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_StoredInField()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("StoredInField", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_StoredInOutParam()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("StoredInOutParam", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_UnderscoreDiscard()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("DiscardWithUnderscore", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_TestProjectMembers()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        var testProjectNames = RoslynCodeLens.TestDiscovery.TestProjectDetector
            .GetTestProjectIds(_loaded.Solution)
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(result.Violations, v => testProjectNames.Contains(v.Project));
    }

    [Fact]
    public void Summary_TotalMatchesListLength()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        Assert.Equal(result.Violations.Count, result.Summary.TotalViolations);
    }

    [Fact]
    public void Summary_ByPatternCountsAreCorrect()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        foreach (var (patternName, count) in result.Summary.ByPattern)
        {
            var actual = result.Violations.Count(v => v.Pattern.ToString() == patternName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Summary_BySeverityCountsAreCorrect()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        foreach (var (severityName, count) in result.Summary.BySeverity)
        {
            var actual = result.Violations.Count(v => v.Severity.ToString() == severityName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Violations_SortedBySeverityThenLocation()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);

        for (int i = 1; i < result.Violations.Count; i++)
        {
            var prev = result.Violations[i - 1];
            var curr = result.Violations[i];

            Assert.True((int)prev.Severity <= (int)curr.Severity, $"Severity sort violation at {i}");

            if (prev.Severity == curr.Severity)
            {
                var pathCmp = string.CompareOrdinal(prev.FilePath, curr.FilePath);
                Assert.True(pathCmp <= 0, $"FilePath sort violation at {i}");
                if (pathCmp == 0)
                    Assert.True(prev.Line <= curr.Line, $"Line sort violation at {i}");
            }
        }
    }

    [Fact]
    public void Violations_HaveLocationAndSnippet()
    {
        var result = FindDisposableMisuseLogic.Execute(_loaded, _resolver);
        foreach (var v in result.Violations)
        {
            Assert.NotEmpty(v.FilePath);
            Assert.True(v.Line > 0);
            Assert.NotEmpty(v.Project);
            Assert.NotEmpty(v.Snippet);
            Assert.NotEmpty(v.ContainingMethod);
        }
    }

    [Fact]
    public void LinkedFile_InTwoCompilations_ReportsOneViolation()
    {
        // One physical file, two compilations — a linked file, or equivalently one project
        // multi-targeted across two frameworks. The scan must report what is written once, not once
        // per compilation the file happens to be compiled into.
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("ProjA", [("Shared.cs", LinkedMisuse)]),
            ("ProjB", [("Shared.cs", LinkedMisuse)]));

        var result = FindDisposableMisuseLogic.Execute(loaded, resolver);

        Assert.Single(result.Violations);
        Assert.Equal(DisposableMisusePattern.DisposableNotDisposed, result.Violations[0].Pattern);
    }

    private const string LinkedMisuse = """
        using System;

        namespace Linked;

        public sealed class Handle : IDisposable
        {
            public void Dispose() { }
        }

        public class Leaky
        {
            public void Leak()
            {
                var handle = new Handle();
            }
        }
        """;
}
