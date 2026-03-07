using Microsoft.CodeAnalysis;

namespace RoslynCodeGraph.Tests;

public class FileChangeTrackerTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private string _solutionPath = null!;

    public async Task InitializeAsync()
    {
        _solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(_solutionPath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Constructor_BuildsReverseDependencyGraph()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        Assert.False(tracker.HasStaleProjects);
        tracker.Dispose();
    }

    [Fact]
    public void MarkProjectStale_SetsHasStaleProjects()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var projectId = _loaded.Solution.Projects.First().Id;

        tracker.MarkProjectStale(projectId);

        Assert.True(tracker.HasStaleProjects);
        Assert.Contains(projectId, tracker.StaleProjectIds);
        tracker.Dispose();
    }

    [Fact]
    public void MarkProjectStale_IncludesTransitiveDependents()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var projects = _loaded.Solution.Projects.ToList();
        var depended = projects.FirstOrDefault(p =>
            projects.Any(other => other.ProjectReferences.Any(r => r.ProjectId == p.Id)));

        if (depended != null)
        {
            tracker.MarkProjectStale(depended.Id);

            var dependents = projects
                .Where(p => p.ProjectReferences.Any(r => r.ProjectId == depended.Id))
                .Select(p => p.Id);

            foreach (var dep in dependents)
                Assert.Contains(dep, tracker.StaleProjectIds);
        }
        tracker.Dispose();
    }

    [Fact]
    public void ClearStale_ResetsState()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var projectId = _loaded.Solution.Projects.First().Id;

        tracker.MarkProjectStale(projectId);
        Assert.True(tracker.HasStaleProjects);

        tracker.ClearStale();
        Assert.False(tracker.HasStaleProjects);
        Assert.Empty(tracker.StaleProjectIds);
        tracker.Dispose();
    }

    [Fact]
    public void FindProjectForFile_ReturnsCorrectProject()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var project = _loaded.Solution.Projects.First();
        var doc = project.Documents.FirstOrDefault(d => d.FilePath != null);

        if (doc != null)
        {
            var foundId = tracker.FindProjectForFile(doc.FilePath!);
            Assert.Equal(project.Id, foundId);
        }
        tracker.Dispose();
    }
}
