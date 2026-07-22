namespace RoslynCodeLens.Models;

/// <summary>
/// Which complexity number drives filtering and sorting. Both are always reported.
/// </summary>
public enum ComplexityMetricKind
{
    /// <summary>McCabe cyclomatic complexity — the number of paths. Starts at 1.</summary>
    Cyclomatic,

    /// <summary>Cognitive complexity — how hard the code is to follow. Starts at 0.</summary>
    Cognitive,
}
