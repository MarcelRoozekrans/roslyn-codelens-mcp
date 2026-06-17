namespace RoslynCodeLens;

/// <summary>
/// Optional input to <see cref="SolutionLoader"/>'s open operation.
/// <see cref="Include"/> and <see cref="RootProjects"/> together act as the
/// seed set; the loader walks <c>ProjectReference</c> transitively from
/// these seeds to produce the loaded project set.
/// </summary>
/// <param name="Include">Glob patterns matched against <c>Project.Name</c>.</param>
/// <param name="RootProjects">Exact project names; missing names are an error.</param>
public sealed record ProjectFilter(
    IReadOnlyList<string> Include,
    IReadOnlyList<string> RootProjects)
{
    public bool HasSeeds => Include.Count > 0 || RootProjects.Count > 0;
}
