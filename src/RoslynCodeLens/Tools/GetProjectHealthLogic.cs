using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class GetProjectHealthLogic
{
    public static GetProjectHealthResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        int hotspotsPerDimension)
    {
        var complexity = GetComplexityMetricsLogic.Execute(loaded, resolver, project, threshold: 10);
        var largeClasses = FindLargeClassesLogic.Execute(loaded, resolver, project, maxMembers: 20, maxLines: 500);
        var naming = FindNamingViolationsLogic.Execute(loaded, resolver, project);
        var (unused, _) = FindUnusedSymbolsLogic.Execute(loaded, resolver, project, includeInternal: false);
        var reflection = FindReflectionUsageLogic.Execute(loaded, resolver, symbol: null);
        var async = FindAsyncViolationsLogic.Execute(loaded, resolver).Violations;
        var disposable = FindDisposableMisuseLogic.Execute(loaded, resolver).Violations;

        // Group each dimension's findings by project name once. Avoids O(N×M) re-filtering
        // per project below.
        var fileToProject = BuildFileToProjectMap(loaded);
        var byProjectComplexity = complexity.GroupBy(c => c.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ComplexityMetric>)g.ToList(), StringComparer.Ordinal);
        var byProjectLarge = largeClasses.GroupBy(c => c.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LargeClassInfo>)g.ToList(), StringComparer.Ordinal);
        var byProjectNaming = naming.GroupBy(n => n.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<NamingViolation>)g.ToList(), StringComparer.Ordinal);
        var byProjectUnused = unused.GroupBy(u => u.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UnusedSymbolInfo>)g.ToList(), StringComparer.Ordinal);
        var byProjectReflection = reflection
            .Select(r => (Usage: r, Project: fileToProject.TryGetValue(r.File, out var p) ? p : ""))
            .GroupBy(r => r.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReflectionUsage>)g.Select(x => x.Usage).ToList(), StringComparer.Ordinal);
        var byProjectAsync = async.GroupBy(a => a.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AsyncViolation>)g.ToList(), StringComparer.Ordinal);
        var byProjectDisposable = disposable.GroupBy(d => d.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<DisposableMisuseViolation>)g.ToList(), StringComparer.Ordinal);

        // Outer filter is load-bearing: 5 of the 7 underlying tools (complexity, large classes,
        // naming, unused, reflection) don't filter test projects internally — only async-violations
        // and disposable-misuse do. Removing this would leak test projects into the result.
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        var productionProjects = loaded.Solution.Projects
            .Where(p => !testProjectIds.Contains(p.Id))
            .Select(p => p.Name)
            .Where(name => project is null || string.Equals(name, project, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var entries = new List<ProjectHealth>(productionProjects.Count);
        foreach (var projectName in productionProjects)
        {
            var pComplexity = byProjectComplexity.TryGetValue(projectName, out var c1) ? c1 : [];
            var pLarge = byProjectLarge.TryGetValue(projectName, out var c2) ? c2 : [];
            var pNaming = byProjectNaming.TryGetValue(projectName, out var c3) ? c3 : [];
            var pUnused = byProjectUnused.TryGetValue(projectName, out var c4) ? c4 : [];
            var pReflection = byProjectReflection.TryGetValue(projectName, out var c5) ? c5 : [];
            var pAsync = byProjectAsync.TryGetValue(projectName, out var c6) ? c6 : [];
            var pDisposable = byProjectDisposable.TryGetValue(projectName, out var c7) ? c7 : [];

            var counts = new ProjectHealthCounts(
                ComplexityHotspots: pComplexity.Count,
                LargeClasses: pLarge.Count,
                NamingViolations: pNaming.Count,
                UnusedSymbols: pUnused.Count,
                ReflectionUsages: pReflection.Count,
                AsyncViolations: pAsync.Count,
                DisposableMisuse: pDisposable.Count);

            var n = Math.Max(0, hotspotsPerDimension);
            var hotspots = new ProjectHealthHotspots(
                Complexity: pComplexity.OrderByDescending(c => c.Complexity).Take(n).ToList(),
                LargeClasses: pLarge.OrderByDescending(c => c.LineCount).Take(n).ToList(),
                Naming: pNaming.Take(n).ToList(),
                Unused: pUnused.Take(n).ToList(),
                Reflection: pReflection.Take(n).ToList(),
                Async: pAsync
                    .OrderByDescending(a => (int)a.Severity)
                    .ThenBy(a => a.FilePath, StringComparer.Ordinal)
                    .ThenBy(a => a.Line)
                    .Take(n)
                    .ToList(),
                Disposable: pDisposable
                    .OrderByDescending(d => (int)d.Severity)
                    .ThenBy(d => d.FilePath, StringComparer.Ordinal)
                    .ThenBy(d => d.Line)
                    .Take(n)
                    .ToList());

            entries.Add(new ProjectHealth(projectName, counts, hotspots));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Project, b.Project));
        return new GetProjectHealthResult(entries);
    }

    private static Dictionary<string, string> BuildFileToProjectMap(LoadedSolution loaded)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in loaded.Solution.Projects)
        {
            foreach (var doc in p.Documents)
            {
                if (!string.IsNullOrEmpty(doc.FilePath))
                    map[doc.FilePath] = p.Name;
            }
        }
        return map;
    }
}
