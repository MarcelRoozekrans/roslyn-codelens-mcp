namespace RoslynCodeLens;

/// <summary>
/// Optional input to <see cref="SolutionLoader"/>'s open operation.
/// <see cref="Include"/> and <see cref="RootProjects"/> together act as the
/// seed set; the loader walks <c>ProjectReference</c> transitively from
/// these seeds to produce the loaded project set.
/// </summary>
/// <param name="Include">Glob patterns matched (case-insensitively) against the project's
/// file name without extension — the <c>.csproj</c>/<c>.vbproj</c> base name — not the
/// MSBuild <c>&lt;AssemblyName&gt;</c>.</param>
/// <param name="RootProjects">Exact project file names without extension (the
/// <c>.csproj</c>/<c>.vbproj</c> base name), not the assembly name; missing names are an error.</param>
public sealed record ProjectFilter(
    IReadOnlyList<string> Include,
    IReadOnlyList<string> RootProjects)
{
    public bool HasSeeds => Include.Count > 0 || RootProjects.Count > 0;
}
