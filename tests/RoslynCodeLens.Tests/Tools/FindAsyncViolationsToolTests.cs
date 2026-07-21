using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindAsyncViolationsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindAsyncViolationsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Theory]
    [InlineData(AsyncViolationPattern.SyncOverAsyncResult, "GetResultViolation")]
    [InlineData(AsyncViolationPattern.SyncOverAsyncWait, "WaitViolation")]
    [InlineData(AsyncViolationPattern.SyncOverAsyncGetAwaiterGetResult, ".GetAwaiterGetResultViolation")]
    [InlineData(AsyncViolationPattern.AsyncVoid, "AsyncVoidViolation")]
    [InlineData(AsyncViolationPattern.MissingAwait, "MissingAwaitViolation")]
    [InlineData(AsyncViolationPattern.FireAndForget, "FireAndForgetViolation")]
    public void DetectsExactlyOneViolationPerPattern(AsyncViolationPattern pattern, string methodName)
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        var hits = result.Violations.Where(v =>
            v.Pattern == pattern &&
            v.ContainingMethod.EndsWith(methodName, StringComparison.Ordinal)).ToList();

        Assert.Single(hits);
    }

    [Fact]
    public void DetectsConfiguredAwaiter_GetResult()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Violations, v =>
            v.Pattern == AsyncViolationPattern.SyncOverAsyncGetAwaiterGetResult &&
            v.ContainingMethod.EndsWith("ConfiguredGetAwaiterGetResultViolation", StringComparison.Ordinal));
    }

    [Fact]
    public void DetectsResult_OnValueTask()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.Contains(result.Violations, v =>
            v.Pattern == AsyncViolationPattern.SyncOverAsyncResult &&
            v.ContainingMethod.EndsWith("GetResultOnValueTaskViolation", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_ProperlyAwaited()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("ProperAwait", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_EventHandlerAsyncVoid()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("EventHandler", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_DiscardOrAssignedTask()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("DiscardFireAndForget", StringComparison.Ordinal) ||
            v.ContainingMethod.EndsWith("AssignedFireAndForget", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_ForwardingReturn()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.DoesNotContain(result.Violations, v =>
            v.ContainingMethod.EndsWith("ForwardingMethod", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotFlag_TestProjectMembers()
    {
        // Test fixtures may contain async patterns; they must be skipped.
        // Use TestProjectDetector to determine which projects are test projects (matches production semantics).
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);
        var testProjectNames = RoslynCodeLens.TestDiscovery.TestProjectDetector
            .GetTestProjectIds(_loaded.Solution)
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(result.Violations, v => testProjectNames.Contains(v.Project));
    }

    [Fact]
    public void Summary_TotalMatchesListLength()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        Assert.Equal(result.Violations.Count, result.Summary.TotalViolations);
    }

    [Fact]
    public void Summary_ByPatternCountsAreCorrect()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        foreach (var kvp in result.Summary.ByPattern)
        {
            var actual = result.Violations.Count(v => v.Pattern.ToString() == kvp.Key);
            Assert.Equal(actual, kvp.Value);
        }
    }

    [Fact]
    public void Summary_BySeverityCountsAreCorrect()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        foreach (var kvp in result.Summary.BySeverity)
        {
            var actual = result.Violations.Count(v => v.Severity.ToString() == kvp.Key);
            Assert.Equal(actual, kvp.Value);
        }
    }

    [Fact]
    public void Violations_SortedBySeverityThenLocation()
    {
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

        for (int i = 1; i < result.Violations.Count; i++)
        {
            var prev = result.Violations[i - 1];
            var curr = result.Violations[i];

            Assert.True((int)prev.Severity <= (int)curr.Severity,
                $"Severity sort violation at {i}");

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
        var result = FindAsyncViolationsLogic.Execute(_loaded, _resolver);

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
            ("ProjA", [("Shared.cs", LinkedViolation)]),
            ("ProjB", [("Shared.cs", LinkedViolation)]));

        var result = FindAsyncViolationsLogic.Execute(loaded, resolver);

        Assert.Single(result.Violations);
        Assert.Equal(AsyncViolationPattern.SyncOverAsyncResult, result.Violations[0].Pattern);
    }

    private const string LinkedViolation = """
        using System.Threading.Tasks;

        namespace Linked;

        public class Blocking
        {
            public int Block(Task<int> work) => work.Result;
        }
        """;
}
