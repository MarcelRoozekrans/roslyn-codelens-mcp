using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynCodeLens.Analyzers;

namespace RoslynCodeLens.Tests.Analyzers;

public class AnalyzerReferenceRemapperTests
{
    [Fact]
    public void Remap_RewritesFileReferences_PreservingPaths()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        using var loader = new ShadowCopyAnalyzerAssemblyLoader(cache);
        var path = typeof(System.Text.Json.JsonSerializer).Assembly.Location;
        var input = new AnalyzerReference[] { new AnalyzerFileReference(path, loader) };

        var result = AnalyzerReferenceRemapper.Remap(input, loader);

        var afr = Assert.IsType<AnalyzerFileReference>(Assert.Single(result));
        Assert.Equal(path, afr.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remap_PassesThroughNonFileReferences()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        using var loader = new ShadowCopyAnalyzerAssemblyLoader(cache);
        var stub = new StubReference();

        var result = AnalyzerReferenceRemapper.Remap(new AnalyzerReference[] { stub }, loader);

        Assert.Same(stub, Assert.Single(result));
    }

    private sealed class StubReference : AnalyzerReference
    {
        public override string? FullPath => null;
        public override object Id => "stub";
        public override System.Collections.Immutable.ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override System.Collections.Immutable.ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
    }
}
