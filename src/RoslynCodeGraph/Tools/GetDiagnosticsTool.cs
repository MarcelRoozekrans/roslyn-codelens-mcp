using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetDiagnosticsLogic
{
    public static List<DiagnosticInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, string? severity)
    {
        return CollectCompilerDiagnostics(loaded, resolver, project, severity);
    }

    public static async Task<List<DiagnosticInfo>> ExecuteAsync(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        string? severity,
        bool includeAnalyzers,
        CancellationToken ct = default)
    {
        var results = CollectCompilerDiagnostics(loaded, resolver, project, severity);

        if (includeAnalyzers)
        {
            var runner = new AnalyzerRunner();
            var minSeverity = ParseMinSeverity(severity);

            foreach (var (projectId, compilation) in loaded.Compilations)
            {
                var projectName = resolver.GetProjectName(projectId);

                if (project != null &&
                    !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                    continue;

                var roslynProject = loaded.Solution.GetProject(projectId);
                if (roslynProject == null)
                    continue;

                var analyzerDiagnostics = await runner.RunAnalyzersAsync(roslynProject, compilation, ct);

                foreach (var diagnostic in analyzerDiagnostics)
                {
                    if (diagnostic.Severity < minSeverity)
                        continue;

                    var lineSpan = diagnostic.Location.GetLineSpan();
                    var file = lineSpan.Path ?? "";
                    var line = lineSpan.StartLinePosition.Line + 1;

                    results.Add(new DiagnosticInfo(
                        diagnostic.Id,
                        diagnostic.Severity.ToString(),
                        diagnostic.GetMessage(),
                        file,
                        line,
                        projectName,
                        $"analyzer:{diagnostic.Id}"));
                }
            }
        }

        return results;
    }

    private static DiagnosticSeverity ParseMinSeverity(string? severity)
    {
        return severity?.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Warning
        };
    }

    private static List<DiagnosticInfo> CollectCompilerDiagnostics(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        string? severity)
    {
        var minSeverity = ParseMinSeverity(severity);
        var results = new List<DiagnosticInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);

            if (project != null &&
                !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var diagnostic in compilation.GetDiagnostics())
            {
                if (diagnostic.Severity < minSeverity)
                    continue;

                var lineSpan = diagnostic.Location.GetLineSpan();
                var file = lineSpan.Path ?? "";
                var line = lineSpan.StartLinePosition.Line + 1;

                results.Add(new DiagnosticInfo(
                    diagnostic.Id,
                    diagnostic.Severity.ToString(),
                    diagnostic.GetMessage(),
                    file,
                    line,
                    projectName));
            }
        }

        return results;
    }
}

[McpServerToolType]
public static class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics"),
     Description("List compiler errors and warnings across the solution, optionally including analyzer diagnostics")]
    public static async Task<List<DiagnosticInfo>> Execute(
        SolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum severity: 'error' or 'warning' (default: warning)")] string? severity = null,
        [Description("Include analyzer diagnostics (default: true)")] bool includeAnalyzers = true,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await GetDiagnosticsLogic.ExecuteAsync(manager.GetLoadedSolution(), manager.GetResolver(), project, severity, includeAnalyzers, ct);
    }
}
