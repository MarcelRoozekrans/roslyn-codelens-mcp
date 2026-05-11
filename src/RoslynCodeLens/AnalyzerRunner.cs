using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynCodeLens.Security;

namespace RoslynCodeLens;

public static class AnalyzerRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<ImmutableArray<Diagnostic>> RunAnalyzersAsync(
        Project project,
        Compilation compilation,
        AnalyzerAllowlist allowlist,
        CancellationToken ct)
    {
        var analyzers = GetAnalyzers(project, allowlist);
        if (analyzers.IsEmpty)
            return ImmutableArray<Diagnostic>.Empty;

        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, options: null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            var results = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(timeoutCts.Token).ConfigureAwait(false);
            return results;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ImmutableArray<Diagnostic>.Empty;
        }
    }

    private static ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(Project project, AnalyzerAllowlist allowlist)
    {
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var solutionDir = Path.GetDirectoryName(project.Solution.FilePath);
        if (string.IsNullOrEmpty(solutionDir))
            return ImmutableArray<DiagnosticAnalyzer>.Empty;

        foreach (var analyzerRef in project.AnalyzerReferences)
        {
            // FullPath is null for in-memory analyzer references; treat those as not-allowed
            // unless policy is "all" (which the allowlist enforces internally).
            var path = analyzerRef.FullPath;
            if (path is null || !allowlist.IsAllowed(path, solutionDir))
                continue;

            foreach (var analyzer in analyzerRef.GetAnalyzers(project.Language))
                analyzers.Add(analyzer);
        }

        return analyzers.ToImmutable();
    }
}
