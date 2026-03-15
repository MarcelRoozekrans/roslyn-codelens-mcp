using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class AnalyzeChangeImpactLogic
{
    public static ChangeImpact? Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var references = FindReferencesLogic.Execute(loaded, resolver, symbol);
        var callers = FindCallersLogic.Execute(loaded, resolver, symbol);

        // If neither found anything, symbol doesn't exist
        if (references.Count == 0 && callers.Count == 0)
        {
            var targets = resolver.FindSymbols(symbol);
            if (targets.Count == 0)
                return null;
        }

        var affectedFiles = references.Select(r => r.File)
            .Concat(callers.Select(c => c.File))
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList();

        var affectedProjects = references.Select(r => r.Project)
            .Concat(callers.Select(c => c.Project))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList();

        return new ChangeImpact(
            Symbol: symbol,
            DirectReferenceCount: references.Count,
            CallerCount: callers.Count,
            AffectedFiles: affectedFiles,
            AffectedProjects: affectedProjects,
            DirectReferences: references,
            Callers: callers);
    }
}
