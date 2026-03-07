using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynCodeGraph;

public class AnalyzerRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public async Task<ImmutableArray<Diagnostic>> RunAnalyzersAsync(
        Project project,
        Compilation compilation,
        CancellationToken ct)
    {
        var analyzers = GetAnalyzers(project);
        if (analyzers.IsEmpty)
            return ImmutableArray<Diagnostic>.Empty;

        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, options: null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            var results = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(timeoutCts.Token);
            return results;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ImmutableArray<Diagnostic>.Empty;
        }
    }

    private static ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(Project project)
    {
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

        foreach (var analyzerRef in project.AnalyzerReferences)
        {
            foreach (var analyzer in analyzerRef.GetAnalyzers(project.Language))
            {
                analyzers.Add(analyzer);
            }
        }

        return analyzers.ToImmutable();
    }
}
