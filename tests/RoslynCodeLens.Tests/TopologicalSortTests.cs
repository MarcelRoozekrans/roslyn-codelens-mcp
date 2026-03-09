using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Tests;

public class TopologicalSortTests
{
    [Fact]
    public void GetCompilationLevels_ReturnsLeavesFirst()
    {
        // Build a mock solution: A depends on B, B depends on C
        var workspace = new AdhocWorkspace();
        var projectC = workspace.AddProject("C", LanguageNames.CSharp);
        var projectB = workspace.AddProject(
            ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "B", "B", LanguageNames.CSharp)
                .WithProjectReferences([new ProjectReference(projectC.Id)]));
        var projectA = workspace.AddProject(
            ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "A", "A", LanguageNames.CSharp)
                .WithProjectReferences([new ProjectReference(projectB.Id)]));

        var solution = workspace.CurrentSolution;
        var levels = SolutionLoader.GetCompilationLevels(solution);

        // Level 0: C (leaf), Level 1: B, Level 2: A
        Assert.Equal(3, levels.Count);
        Assert.Single(levels[0]);
        Assert.Equal("C", levels[0][0].Name);
        Assert.Single(levels[1]);
        Assert.Equal("B", levels[1][0].Name);
        Assert.Single(levels[2]);
        Assert.Equal("A", levels[2][0].Name);
    }

    [Fact]
    public void GetCompilationLevels_GroupsIndependentProjects()
    {
        // B and C are both leaves, A depends on both
        var workspace = new AdhocWorkspace();
        var projectB = workspace.AddProject("B", LanguageNames.CSharp);
        var projectC = workspace.AddProject("C", LanguageNames.CSharp);
        var projectA = workspace.AddProject(
            ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "A", "A", LanguageNames.CSharp)
                .WithProjectReferences([new ProjectReference(projectB.Id), new ProjectReference(projectC.Id)]));

        var solution = workspace.CurrentSolution;
        var levels = SolutionLoader.GetCompilationLevels(solution);

        // Level 0: B and C (both leaves), Level 1: A
        Assert.Equal(2, levels.Count);
        Assert.Equal(2, levels[0].Count);
        Assert.Contains(levels[0], p => p.Name == "B");
        Assert.Contains(levels[0], p => p.Name == "C");
        Assert.Single(levels[1]);
        Assert.Equal("A", levels[1][0].Name);
    }
}
