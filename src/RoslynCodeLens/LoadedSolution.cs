using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

public class LoadedSolution
{
    public required Solution Solution { get; init; }
    public required ConcurrentDictionary<ProjectId, Compilation> Compilations { get; init; }
    public IReadOnlyList<SkippedProject> SkippedProjects { get; init; } = Array.Empty<SkippedProject>();
    public bool IsEmpty => Compilations.IsEmpty;

    /// <summary>
    /// Reference-resolution failures reported by MSBuildWorkspace while loading (as
    /// opposed to <see cref="SkippedProjects"/>, which are projects never opened).
    /// Non-empty means the load is degraded — some projects opened with dropped
    /// references, so results from those projects may be incomplete. Surfaced so
    /// callers can warn instead of silently returning empty/partial results.
    /// </summary>
    public IReadOnlyList<string> LoadDiagnostics { get; init; } = Array.Empty<string>();
    public bool Degraded => LoadDiagnostics.Count > 0;

    public static LoadedSolution Empty { get; } = CreateEmpty();

    private static LoadedSolution CreateEmpty()
    {
        var workspace = new AdhocWorkspace();
        return new LoadedSolution
        {
            Solution = workspace.CurrentSolution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>()
        };
    }
}

public sealed record SkippedProject(string Path, string Name, string Kind, string Reason);
