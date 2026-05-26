using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetTypeOverviewLogic
{
    /// <summary>
    /// Returns a combined context + hierarchy + diagnostics view for a type, resolving
    /// both source-defined and metadata-only types. For metadata types the diagnostics
    /// list is empty by construction (the compiler does not attach diagnostics to
    /// closed-source assemblies) and derived types are source-only.
    /// </summary>
    public static TypeOverview Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string typeName)
    {
        var context = GetSymbolContextLogic.Execute(loaded, resolver, metadata, typeName);

        var hierarchy = GetTypeHierarchyLogic.Execute(resolver, metadata, typeName);

        List<DiagnosticInfo> diagnostics;
        if (string.Equals(context.Origin?.Kind, "metadata", StringComparison.Ordinal))
        {
            // No diagnostics apply to closed-source types; File is empty so the
            // file-scoped diagnostic filter below would be a no-op anyway.
            diagnostics = [];
        }
        else
        {
            // Diagnostics scoped to the file containing the type
            diagnostics = GetDiagnosticsLogic.Execute(loaded, resolver, project: null, severity: null)
                .Where(d => !string.IsNullOrEmpty(context.File) &&
                            string.Equals(d.File, context.File, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new TypeOverview(context, hierarchy, diagnostics, Origin: context.Origin);
    }
}
