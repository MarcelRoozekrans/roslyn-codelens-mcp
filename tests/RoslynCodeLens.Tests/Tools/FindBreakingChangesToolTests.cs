using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindBreakingChangesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly IReadOnlyList<PublicApiEntry> _currentSurface;

    public FindBreakingChangesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _currentSurface = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver).Entries;
    }

    [Fact]
    public void Diff_NoChanges_ReturnsEmpty()
    {
        var result = FindBreakingChangesLogic.Diff(_currentSurface, _currentSurface);

        Assert.Empty(result.Changes);
        Assert.Equal(0, result.Summary.TotalChanges);
    }

    [Fact]
    public void Diff_SymbolRemoved_ReportedAsRemovedBreaking()
    {
        var fakeRemoved = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed.Method()", PublicApiAccessibility.Public,
            "FakeProj", "Fake.cs", 1);
        var baseline = _currentSurface.Concat(new[] { fakeRemoved }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        var removed = Assert.Single(result.Changes, c => c.Name == "Fake.Removed.Method()");
        Assert.Equal(BreakingChangeKind.Removed, removed.Kind);
        Assert.Equal(BreakingChangeSeverity.Breaking, removed.Severity);
    }

    [Fact]
    public void Diff_SymbolAdded_ReportedAsAddedNonBreaking()
    {
        var dropped = _currentSurface.First(e => e.Name == "TestLib.Greeter");
        var baseline = _currentSurface.Where(e => e.Name != dropped.Name).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        var added = Assert.Single(result.Changes, c => c.Name == dropped.Name);
        Assert.Equal(BreakingChangeKind.Added, added.Kind);
        Assert.Equal(BreakingChangeSeverity.NonBreaking, added.Severity);
    }

    [Fact]
    public void Diff_KindChanged_ReportedAsKindChangedBreaking()
    {
        var greeter = _currentSurface.First(e => e.Name == "TestLib.Greeter");
        var baselineMutated = _currentSurface
            .Where(e => e.Name != greeter.Name)
            .Concat(new[] { greeter with { Kind = PublicApiKind.Struct } })
            .ToList();

        var result = FindBreakingChangesLogic.Diff(baselineMutated, _currentSurface);

        var change = Assert.Single(result.Changes, c => c.Name == "TestLib.Greeter");
        Assert.Equal(BreakingChangeKind.KindChanged, change.Kind);
        Assert.Equal(BreakingChangeSeverity.Breaking, change.Severity);
    }

    [Fact]
    public void Diff_AccessibilityNarrowed_Breaking()
    {
        var processProtected = _currentSurface.First(e =>
            e.Name.Contains("AbstractProcessor.Process", StringComparison.Ordinal));
        var baselineMutated = _currentSurface
            .Where(e => e.Name != processProtected.Name)
            .Concat(new[] { processProtected with { Accessibility = PublicApiAccessibility.Public } })
            .ToList();

        var result = FindBreakingChangesLogic.Diff(baselineMutated, _currentSurface);

        var change = Assert.Single(result.Changes, c => c.Name == processProtected.Name);
        Assert.Equal(BreakingChangeKind.AccessibilityNarrowed, change.Kind);
        Assert.Equal(BreakingChangeSeverity.Breaking, change.Severity);
    }

    [Fact]
    public void Diff_AccessibilityWidened_NonBreaking()
    {
        var greet = _currentSurface.First(e => e.Name == "TestLib.Greeter.Greet(string)");
        var baselineMutated = _currentSurface
            .Where(e => e.Name != greet.Name)
            .Concat(new[] { greet with { Accessibility = PublicApiAccessibility.Protected } })
            .ToList();

        var result = FindBreakingChangesLogic.Diff(baselineMutated, _currentSurface);

        var change = Assert.Single(result.Changes, c => c.Name == greet.Name);
        Assert.Equal(BreakingChangeKind.AccessibilityWidened, change.Kind);
        Assert.Equal(BreakingChangeSeverity.NonBreaking, change.Severity);
    }

    [Fact]
    public void Severity_BreakingBeforeNonBreaking_InSort()
    {
        var fake1 = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed1()", PublicApiAccessibility.Public, "P", "f", 1);
        var fake2 = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed2()", PublicApiAccessibility.Public, "P", "f", 1);
        var baseline = _currentSurface.Concat(new[] { fake1, fake2 }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        var firstNonBreaking = result.Changes
            .Select((c, i) => new { c, i })
            .FirstOrDefault(x => x.c.Severity == BreakingChangeSeverity.NonBreaking);
        if (firstNonBreaking is not null)
        {
            for (int i = firstNonBreaking.i; i < result.Changes.Count; i++)
                Assert.Equal(BreakingChangeSeverity.NonBreaking, result.Changes[i].Severity);
        }
    }

    [Fact]
    public void Within_Severity_NameOrdinal()
    {
        var fakeA = new PublicApiEntry(
            PublicApiKind.Method, "AAAA.Removed()", PublicApiAccessibility.Public, "P", "f", 1);
        var fakeZ = new PublicApiEntry(
            PublicApiKind.Method, "ZZZZ.Removed()", PublicApiAccessibility.Public, "P", "f", 1);
        var baseline = _currentSurface.Concat(new[] { fakeZ, fakeA }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        for (int i = 1; i < result.Changes.Count; i++)
        {
            var prev = result.Changes[i - 1];
            var curr = result.Changes[i];
            if (prev.Severity == curr.Severity)
            {
                Assert.True(string.CompareOrdinal(prev.Name, curr.Name) <= 0,
                    $"Sort violation at {i}: '{prev.Name}' > '{curr.Name}'");
            }
        }
    }

    [Fact]
    public void Summary_TotalMatchesListLength()
    {
        var fake = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed()", PublicApiAccessibility.Public, "P", "f", 1);
        var baseline = _currentSurface.Concat(new[] { fake }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        Assert.Equal(result.Changes.Count, result.Summary.TotalChanges);
    }

    [Fact]
    public void Summary_ByKindAndBySeverityCountsAreCorrect()
    {
        var fake = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Removed()", PublicApiAccessibility.Public, "P", "f", 1);
        var baseline = _currentSurface.Concat(new[] { fake }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        foreach (var (kindName, count) in result.Summary.ByKind)
        {
            var actual = result.Changes.Count(c => c.Kind.ToString() == kindName);
            Assert.Equal(actual, count);
        }
        foreach (var (severityName, count) in result.Summary.BySeverity)
        {
            var actual = result.Changes.Count(c => c.Severity.ToString() == severityName);
            Assert.Equal(actual, count);
        }
    }

    [Fact]
    public void Diff_DuplicateFqnInBaseline_CollapsedToSingleChange()
    {
        // Producer bug guard: if a baseline has duplicate FQN entries (e.g., partial classes
        // emitted twice), the diff must not double-report them.
        var fake = new PublicApiEntry(
            PublicApiKind.Method, "Fake.Dup.Method()", PublicApiAccessibility.Public,
            "FakeProj", "Fake.cs", 1);
        var baseline = _currentSurface.Concat(new[] { fake, fake }).ToList();

        var result = FindBreakingChangesLogic.Diff(baseline, _currentSurface);

        var matches = result.Changes.Where(c => c.Name == "Fake.Dup.Method()").ToList();
        Assert.Single(matches);
        Assert.Equal(BreakingChangeKind.Removed, matches[0].Kind);
    }

    [Fact]
    public void Diff_DuplicateFqnInCurrent_CollapsedToSingleChange()
    {
        var fake = new PublicApiEntry(
            PublicApiKind.Method, "Fake.NewDup.Method()", PublicApiAccessibility.Public,
            "FakeProj", "Fake.cs", 1);
        var current = _currentSurface.Concat(new[] { fake, fake }).ToList();

        var result = FindBreakingChangesLogic.Diff(_currentSurface, current);

        var matches = result.Changes.Where(c => c.Name == "Fake.NewDup.Method()").ToList();
        Assert.Single(matches);
        Assert.Equal(BreakingChangeKind.Added, matches[0].Kind);
    }

    [Fact]
    public void Json_Baseline_IdentityRoundtrip()
    {
        var path = WriteBaselineJson(_currentSurface);
        try
        {
            var result = FindBreakingChangesLogic.Execute(_loaded, _resolver, path);
            Assert.Empty(result.Changes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Assembly_Baseline_RoundtripsCleanly()
    {
        var src = """
            using System.Threading.Tasks;
            namespace BaselineProbe
            {
                public class Foo
                {
                    public void Bar() {}
                    public string Echo(string input, int count) => input;
                    public Task<int> Counted(int n) => Task.FromResult(n);
                }
            }
            """;
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "baseline-test",
            new[] { CSharpSyntaxTree.ParseText(src) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dll");
        try
        {
            using (var fs = File.Create(dllPath))
            {
                var emitResult = compilation.Emit(fs);
                Assert.True(emitResult.Success, $"Emit failed: {string.Join(", ", emitResult.Diagnostics)}");
            }

            var result = FindBreakingChangesLogic.Execute(_loaded, _resolver, dllPath);

            Assert.Contains(result.Changes, c =>
                c.Name == "BaselineProbe.Foo" && c.Kind == BreakingChangeKind.Removed);
            Assert.Contains(result.Changes, c =>
                c.Name.Contains("BaselineProbe.Foo.Bar", StringComparison.Ordinal) &&
                c.Kind == BreakingChangeKind.Removed);

            // Methods whose signatures reference BCL types must resolve cleanly — without
            // BCL refs in LoadBaselineFromAssembly's compilation, parameter and return types
            // would render as ?-error symbols and the FQN would lose its parameter list.
            Assert.Contains(result.Changes, c =>
                c.Name == "BaselineProbe.Foo.Echo(string, int)" &&
                c.Kind == BreakingChangeKind.Removed);
            Assert.Contains(result.Changes, c =>
                c.Name == "BaselineProbe.Foo.Counted(int)" &&
                c.Kind == BreakingChangeKind.Removed);
        }
        finally
        {
            if (File.Exists(dllPath)) File.Delete(dllPath);
        }
    }

    [Fact]
    public void MissingBaselineFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var ex = Assert.Throws<McpToolException>(() =>
            FindBreakingChangesLogic.Execute(_loaded, _resolver, path));
        Assert.Equal(ToolErrorCode.FileNotFound, ex.Code);
    }

    [Fact]
    public void MalformedBaselineJson_ThrowsInvalidOperation()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, "{ this is not valid json");
        try
        {
            var ex = Assert.Throws<McpToolException>(() =>
                FindBreakingChangesLogic.Execute(_loaded, _resolver, path));
            Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnknownExtension_ThrowsInvalidOperation()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        File.WriteAllText(path, "<root/>");
        try
        {
            var ex = Assert.Throws<McpToolException>(() =>
                FindBreakingChangesLogic.Execute(_loaded, _resolver, path));
            Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteBaselineJson(IReadOnlyList<PublicApiEntry> entries)
    {
        var byKind = entries
            .GroupBy(e => e.Kind.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byProject = entries
            .GroupBy(e => e.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byAccessibility = entries
            .GroupBy(e => e.Accessibility.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var summary = new PublicApiSummary(entries.Count, byKind, byProject, byAccessibility);
        var result = new GetPublicApiSurfaceResult(summary, entries);

        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        opts.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(result, opts);
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
