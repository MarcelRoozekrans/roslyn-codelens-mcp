using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynCodeLens.Analyzers;

/// <summary>
/// Rebuilds a project's <see cref="AnalyzerFileReference"/>s so they resolve their assemblies
/// through our <see cref="ShadowCopyAnalyzerAssemblyLoader"/> instead of the default (locking)
/// MSBuildWorkspace loader. Non-file references are passed through unchanged.
/// </summary>
public static class AnalyzerReferenceRemapper
{
    public static IReadOnlyList<AnalyzerReference> Remap(
        IEnumerable<AnalyzerReference> references, ShadowCopyAnalyzerAssemblyLoader loader)
    {
        var materialized = references as ICollection<AnalyzerReference> ?? references.ToList();

        // Pre-register every analyzer path so cross-analyzer dependency resolution can find siblings.
        foreach (var r in materialized)
            if (r is AnalyzerFileReference fr)
                loader.AddDependencyLocation(fr.FullPath);

        var result = new List<AnalyzerReference>(materialized.Count);
        foreach (var r in materialized)
        {
            result.Add(r is AnalyzerFileReference fr
                ? new AnalyzerFileReference(fr.FullPath, loader)
                : r);
        }
        return result;
    }
}
