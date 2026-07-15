using RoslynCodeLens.Metadata;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens;

/// <summary>
/// An internally-consistent snapshot of a solution's analysis state: the loaded solution and
/// both resolvers, all guaranteed to come from the same rebuild. Obtained via
/// <see cref="SolutionManager.GetAnalysisContext"/> so callers never mix a solution with a
/// resolver from a different snapshot (the identity mismatch behind #282).
/// </summary>
public readonly record struct SolutionAnalysisContext(
    LoadedSolution Loaded,
    SymbolResolver Resolver,
    MetadataSymbolResolver Metadata);
