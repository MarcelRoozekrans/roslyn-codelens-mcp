using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tools;

/// <summary>
/// What a change to a positional record's primary constructor does beyond the construction sites
/// Roslyn's engine rewrites. The record's parameter list also declares its generated properties
/// and its generated <c>Deconstruct</c>, so:
/// <list type="bullet">
/// <item>a removed parameter removes a property, and every use of it stops compiling;</item>
/// <item>add and remove change the arity of every deconstruction and positional pattern;</item>
/// <item>a reorder moves values between positions — with two same-typed parameters that still
/// compiles and silently binds the other value, which no conflict check can catch;</item>
/// <item>string literals and serialized names are text, so nothing rewrites them.</item>
/// </list>
/// Find-references on <c>Deconstruct</c> misses positional patterns, so the sites come from a
/// syntax walk over the record's project and every project that depends on it.
/// </summary>
internal static class PositionalRecordImpact
{
    public static async Task<IReadOnlyList<string>> AnalyzeAsync(
        LoadedSolution loaded, IMethodSymbol target, IReadOnlyList<DesiredParameter> desired,
        CancellationToken ct)
    {
        var record = target.ContainingType;
        if (record is not { IsRecord: true } || !ChangeSignatureLogic.IsPrimaryConstructor(target))
            return [];

        var original = target.Parameters;
        var unchanged = desired.Count == original.Length
            && desired.Select((p, i) => SymbolEqualityComparer.Default.Equals(p.Existing, original[i]))
                .All(same => same);
        if (unchanged)
            return [];

        var warnings = new List<string>();
        warnings.AddRange(await RemovedPropertyWarningsAsync(loaded, record, original, desired, ct)
            .ConfigureAwait(false));
        warnings.AddRange(await PositionalSiteWarningsAsync(loaded, target, record, original, desired, ct)
            .ConfigureAwait(false));
        warnings.Add(
            $"Text is not rewritten: string literals, JSON or other serialized property names, and "
                + $"comments that name or order {record.Name}'s members keep their old form. Search "
                + "for them before relying on the build: they compile either way.");
        return warnings;
    }

    // ------------------------------------------------------------------ removed properties

    private static async Task<List<string>> RemovedPropertyWarningsAsync(
        LoadedSolution loaded, INamedTypeSymbol record, IReadOnlyList<IParameterSymbol> original,
        IReadOnlyList<DesiredParameter> desired, CancellationToken ct)
    {
        var warnings = new List<string>();
        var removed = original.Where(p => !desired.Any(
            d => SymbolEqualityComparer.Default.Equals(d.Existing, p)));

        foreach (var parameter in removed)
        {
            var property = FindGeneratedProperty(record, parameter);
            if (property == null)
                continue;   // declared by hand: it survives, and its initializer shows up as a conflict

            var sites = await FindReferenceSitesAsync(loaded, property, ct).ConfigureAwait(false);
            var prefix = $"Removing '{parameter.Name}' also removes the generated property "
                + $"{record.Name}.{property.Name}";
            warnings.Add(sites.Count == 0
                ? prefix + "; it has no references in source."
                : prefix + $"; {sites.Count} reference(s), `with` initializers included, will no "
                    + "longer compile: " + string.Join(", ", sites) + ".");
        }

        return warnings;
    }

    /// <summary>
    /// The positional property the compiler generated from a parameter, as opposed to one
    /// declared by hand with the same name, which outlives the parameter.
    /// </summary>
    private static IPropertySymbol? FindGeneratedProperty(
        INamedTypeSymbol record, IParameterSymbol parameter)
        => record.GetMembers(parameter.Name).OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.IsImplicitlyDeclared
                || p.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is ParameterSyntax));

    private static async Task<List<string>> FindReferenceSitesAsync(
        LoadedSolution loaded, IPropertySymbol property, CancellationToken ct)
    {
        var references = await SymbolFinder.FindReferencesAsync(
            property, loaded.Solution, ct).ConfigureAwait(false);
        return references
            .SelectMany(r => r.Locations)
            .Where(l => l.Location.IsInSource)
            .Select(l => FormatLocation(l.Location))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------ positional sites

    private static async Task<List<string>> PositionalSiteWarningsAsync(
        LoadedSolution loaded, IMethodSymbol target, INamedTypeSymbol record,
        IReadOnlyList<IParameterSymbol> original, IReadOnlyList<DesiredParameter> desired,
        CancellationToken ct)
    {
        var deconstruct = record.GetMembers(WellKnownMemberNames.DeconstructMethodName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.Length == original.Count
                && m.Parameters.All(p => p.RefKind == RefKind.Out)
                && !m.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is MethodDeclarationSyntax));
        if (deconstruct == null)
            return [];

        var effect = DescribeEffect(record, original, desired);
        var sites = new SortedSet<(string File, int Line, string Kind)>();

        var declaringProject = target.DeclaringSyntaxReferences
            .Select(r => loaded.Solution.GetDocument(r.SyntaxTree)?.Project.Id)
            .FirstOrDefault(id => id != null);
        if (declaringProject == null)
            return [];

        var projectIds = new List<ProjectId> { declaringProject };
        projectIds.AddRange(loaded.Solution.GetProjectDependencyGraph()
            .GetProjectsThatTransitivelyDependOnThisProject(declaringProject));

        foreach (var projectId in projectIds)
        {
            var compilation = await GetCompilationAsync(loaded, projectId, ct).ConfigureAwait(false);
            if (compilation == null)
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                ct.ThrowIfCancellationRequested();
                var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
                SemanticModel? model = null;

                foreach (var node in root.DescendantNodes())
                {
                    var kind = node switch
                    {
                        RecursivePatternSyntax { PositionalPatternClause: not null } => "positional pattern",
                        AssignmentExpressionSyntax { Left: TupleExpressionSyntax or DeclarationExpressionSyntax }
                            => "deconstruction",
                        ForEachVariableStatementSyntax => "deconstruction",
                        _ => null,
                    };
                    if (kind == null)
                        continue;

                    model ??= compilation.GetSemanticModel(tree);
                    if (Deconstructs(model, node, deconstruct, ct))
                    {
                        var span = tree.GetLineSpan(node.Span, ct);
                        sites.Add((span.Path, span.StartLinePosition.Line + 1, kind));
                    }
                }
            }
        }

        return sites
            .Select(s => $"{s.File}:{s.Line}: this {s.Kind} binds {record.Name} by position; {effect}")
            .ToList();
    }

    private static bool Deconstructs(
        SemanticModel model, SyntaxNode node, IMethodSymbol deconstruct, CancellationToken ct)
    {
        switch (node)
        {
            case RecursivePatternSyntax pattern:
                return model.GetOperation(pattern, ct) is IRecursivePatternOperation operation
                    && IsSame(operation.DeconstructSymbol, deconstruct);
            case AssignmentExpressionSyntax assignment:
                return Uses(model.GetDeconstructionInfo(assignment), deconstruct);
            case ForEachVariableStatementSyntax forEach:
                return Uses(model.GetDeconstructionInfo(forEach), deconstruct);
            default:
                return false;
        }
    }

    /// <summary>A nested deconstruction such as `(x, (a, b)) = (1, record)` counts too.</summary>
    private static bool Uses(DeconstructionInfo info, IMethodSymbol deconstruct)
        => IsSame(info.Method, deconstruct) || info.Nested.Any(n => Uses(n, deconstruct));

    private static bool IsSame(ISymbol? candidate, IMethodSymbol deconstruct)
        => candidate != null
            && SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, deconstruct.OriginalDefinition);

    /// <summary>
    /// What every positional site experiences, phrased once: an arity change is a compile error
    /// (and already reported as a conflict); a reorder names each position whose value changes,
    /// calling out the same-typed ones that compile and bind the wrong value.
    /// </summary>
    private static string DescribeEffect(
        INamedTypeSymbol record, IReadOnlyList<IParameterSymbol> original,
        IReadOnlyList<DesiredParameter> desired)
    {
        if (desired.Count != original.Count)
        {
            return $"{record.Name}'s Deconstruct goes from {original.Count} to {desired.Count} "
                + "element(s), so it will not compile until it is updated.";
        }

        var moves = new List<string>();
        var silent = false;
        for (var i = 0; i < original.Count; i++)
        {
            var before = original[i];
            var after = desired[i];
            if (SymbolEqualityComparer.Default.Equals(after.Existing, before))
                continue;

            var afterType = after.Existing?.Type ?? after.Type;
            var sameType = afterType != null
                && SymbolEqualityComparer.Default.Equals(afterType, before.Type);
            silent |= sameType;
            moves.Add(sameType
                ? $"position {i + 1} now holds '{after.Name}' instead of '{before.Name}', the same type "
                    + before.Type.ToDisplayString()
                : $"position {i + 1} now holds '{after.Name}' ({afterType?.ToDisplayString() ?? after.TypeName}) "
                    + $"instead of '{before.Name}' ({before.Type.ToDisplayString()})");
        }

        var consequence = silent
            ? "this still compiles but binds the other value, so update it by hand."
            : "it compiles only through `var`, discards or an implicit conversion, and then binds "
                + "the other value, so update it by hand.";
        return string.Join("; ", moves) + ": " + consequence;
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<Compilation?> GetCompilationAsync(
        LoadedSolution loaded, ProjectId projectId, CancellationToken ct)
    {
        if (loaded.Compilations.TryGetValue(projectId, out var cached))
            return cached;

        var project = loaded.Solution.GetProject(projectId);
        return project == null
            ? null
            : await project.GetCompilationAsync(ct).ConfigureAwait(false);
    }

    private static string FormatLocation(Location location)
    {
        var span = location.GetLineSpan();
        return $"{span.Path}:{span.StartLinePosition.Line + 1}";
    }
}
