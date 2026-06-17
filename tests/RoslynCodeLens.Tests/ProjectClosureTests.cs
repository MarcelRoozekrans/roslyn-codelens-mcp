using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class ProjectClosureTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Graph(
        params (string From, string[] To)[] edges)
        => edges.ToDictionary(e => e.From, e => (IReadOnlyList<string>)e.To);

    [Fact]
    public void Closure_FromGlobSeeds_IncludesTransitiveDeps()
    {
        var graph = Graph(
            ("App.Api",         new[] { "App.Domain" }),
            ("App.Domain",      new[] { "Shared.Common" }),
            ("Shared.Common",   Array.Empty<string>()),
            ("Sample.Unrelated", Array.Empty<string>()));

        var filter = new ProjectFilter(Include: new[] { "App.*" }, RootProjects: Array.Empty<string>());
        var result = ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph);

        Assert.Equal(new[] { "App.Api", "App.Domain", "Shared.Common" }, result.Loaded.OrderBy(x => x));
    }

    [Fact]
    public void Closure_FromRootProjects_IncludesTransitiveDeps()
    {
        var graph = Graph(
            ("App.Api",       new[] { "App.Domain" }),
            ("App.Domain",    new[] { "Shared.Common" }),
            ("Shared.Common", Array.Empty<string>()));

        var filter = new ProjectFilter(Include: Array.Empty<string>(), RootProjects: new[] { "App.Api" });
        var result = ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph);

        Assert.Equal(new[] { "App.Api", "App.Domain", "Shared.Common" }, result.Loaded.OrderBy(x => x));
    }

    [Fact]
    public void Closure_FromBoth_IsUnion()
    {
        var graph = Graph(
            ("App.Api",      new[] { "App.Domain" }),
            ("App.Domain",   Array.Empty<string>()),
            ("Tools.CLI",    Array.Empty<string>()),
            ("Sample.Other", Array.Empty<string>()));

        var filter = new ProjectFilter(Include: new[] { "Tools.*" }, RootProjects: new[] { "App.Api" });
        var result = ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph);

        Assert.Equal(new[] { "App.Api", "App.Domain", "Tools.CLI" }, result.Loaded.OrderBy(x => x));
    }

    [Fact]
    public void Closure_StopsAtCycles()
    {
        var graph = Graph(
            ("A", new[] { "B" }),
            ("B", new[] { "A" }));

        var filter = new ProjectFilter(Include: Array.Empty<string>(), RootProjects: new[] { "A" });
        var result = ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph);

        Assert.Equal(new[] { "A", "B" }, result.Loaded.OrderBy(x => x));
    }

    [Fact]
    public void Closure_EmptySeedSet_Throws()
    {
        var graph = Graph(("Lonely", Array.Empty<string>()));
        var filter = new ProjectFilter(Include: new[] { "DoesNotMatch.*" }, RootProjects: Array.Empty<string>());

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph));
        Assert.Contains("matched 0 projects", ex.Message);
        Assert.Contains("Lonely", ex.Message);
    }

    [Fact]
    public void Closure_UnknownRootProject_ThrowsListingMissing()
    {
        var graph = Graph(("App.Api", Array.Empty<string>()));
        var filter = new ProjectFilter(
            Include: Array.Empty<string>(),
            RootProjects: new[] { "App.Api", "App.Ghost", "App.Phantom" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph));
        Assert.Contains("App.Ghost", ex.Message);
        Assert.Contains("App.Phantom", ex.Message);
        Assert.DoesNotContain("App.Api,", ex.Message);
    }

    [Fact]
    public void Closure_GlobMatchIsCaseInsensitive()
    {
        var graph = Graph(
            ("App.Api", Array.Empty<string>()),
            ("Other",   Array.Empty<string>()));

        var filter = new ProjectFilter(Include: new[] { "app.*" }, RootProjects: Array.Empty<string>());
        var result = ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph);

        Assert.Equal(new[] { "App.Api" }, result.Loaded.OrderBy(x => x));
    }

    [Fact]
    public void Closure_InvalidGlob_Throws()
    {
        var graph = Graph(("App.Api", Array.Empty<string>()));
        var filter = new ProjectFilter(Include: new[] { "App.[" }, RootProjects: Array.Empty<string>());

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph));
        Assert.Contains("App.[", ex.Message);
    }
}
