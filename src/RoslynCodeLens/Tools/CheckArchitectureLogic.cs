using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Evaluates user-supplied layering rules against the solution's real semantic type graph.
/// </summary>
/// <remarks>
/// Two semantics decide whether this tool is usable, and both are deliberate:
/// <list type="number">
/// <item><description><b>allowOnly considers only solution-internal, non-generated targets.</b>
/// Otherwise <c>using System;</c> violates "Api may depend only on Application" — absurd, and it
/// would bury every real finding; likewise generator output (regex implementations, JSON contexts,
/// DI registries) would be reported as an unlisted dependency the author cannot remove. Its target
/// set is open-ended, so it only reports targets the author can act on. To restrict a framework or
/// generated namespace, write an explicit <c>forbid</c>; that path names its target and <i>does</i>
/// evaluate metadata and generated ones.</description></item>
/// <item><description><b>Self-references are always allowed.</b> A scope depending on itself is
/// not a layering violation, under either rule kind.</description></item>
/// </list>
/// </remarks>
public static class CheckArchitectureLogic
{
    public const string NamespaceScope = "namespace";
    public const string ProjectScope = "project";
    public const string ForbidKind = "forbid";
    public const string AllowOnlyKind = "allowOnly";

    private sealed class Accumulator
    {
        public int Count;
        public readonly List<ViolationSite> Sites = [];
        public string ToPattern = string.Empty;
    }

    public static IReadOnlyList<ArchitectureViolation> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        IReadOnlyList<ArchitectureRule> rules,
        string scope = NamespaceScope,
        int maxSitesPerViolation = 5,
        CancellationToken cancellationToken = default)
    {
        Validate(rules, scope);

        var byNamespace = string.Equals(scope, NamespaceScope, StringComparison.Ordinal);
        var groups = new Dictionary<(int RuleIndex, string Source, string Target), Accumulator>();
        // A linked or multi-targeted file appears in several compilations; walking it once keeps
        // reference counts honest and project attribution deterministic (see FindThrowSitesLogic).
        var walkedPaths = new HashSet<string>(StringComparer.Ordinal);
        var generatedTargets = new Dictionary<ISymbol, bool>(SymbolEqualityComparer.Default);

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectName = resolver.GetProjectName(projectId);

            // Source-side filtering, part 1: under project scope a whole compilation is either in
            // play or not, so decide before touching its trees.
            if (!byNamespace && !rules.Any(r => ScopePattern.Matches(r.From, projectName)))
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (GeneratedCodeDetector.IsGenerated(tree))
                    continue;

                if (!string.IsNullOrEmpty(tree.FilePath) && !walkedPaths.Add(tree.FilePath))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();

                var root = tree.GetRoot(cancellationToken);

                // Source-side filtering, part 2: if none of the scopes this tree declares can match
                // any rule's From, the tree cannot produce a violation — skip it BEFORE creating a
                // semantic model. This is what keeps cost proportional to the rules written rather
                // than to solution size.
                if (byNamespace && !DeclaredScopes(root).Any(s => rules.Any(r => ScopePattern.Matches(r.From, s))))
                    continue;

                var model = compilation.GetSemanticModel(tree);

                // `using` directives are deliberately NOT walked. A using is not itself a
                // dependency — it is a name-resolution convenience — and counting it breaks the
                // tool twice over: a file-level using is attributed to the GLOBAL namespace (an
                // accusation against a scope that wrote no code), and an in-namespace using or
                // alias double-counts the single real usage below it. Skipping them is also what
                // makes the tool's own promise true: "an unused `using` is not reported".
                foreach (var name in root
                             .DescendantNodes(descendIntoChildren: n => n is not UsingDirectiveSyntax)
                             .OfType<SimpleNameSyntax>())
                {
                    var target = ResolveTargetType(name, model);
                    if (target is null)
                        continue;

                    var sourceScope = byNamespace ? EnclosingNamespace(name) : projectName;
                    var targetScope = byNamespace
                        ? NamespaceOf(target)
                        : TargetProject(target, resolver);

                    // Self-reference: never a violation, under either rule kind.
                    if (string.Equals(sourceScope, targetScope, StringComparison.Ordinal))
                        continue;

                    // allowOnly's target set is open-ended, so it only evaluates targets the
                    // author can actually act on: solution-internal, and not pure generator
                    // output. `forbid` names its target explicitly and evaluates everything.
                    var eligibleForAllowOnly =
                        target.Locations.Any(l => l.IsInSource)
                        && !IsGeneratedOnly(target, generatedTargets);

                    for (var i = 0; i < rules.Count; i++)
                    {
                        var rule = rules[i];
                        if (!ScopePattern.Matches(rule.From, sourceScope))
                            continue;

                        string? toPattern;
                        if (string.Equals(rule.Kind, ForbidKind, StringComparison.Ordinal))
                        {
                            toPattern = rule.To.FirstOrDefault(p => ScopePattern.Matches(p, targetScope));
                            if (toPattern is null)
                                continue;
                        }
                        else
                        {
                            // allowOnly ignores everything outside the solution.
                            if (!eligibleForAllowOnly || rule.To.Any(p => ScopePattern.Matches(p, targetScope)))
                                continue;
                            toPattern = string.Join(", ", rule.To);
                        }

                        var key = (i, sourceScope, targetScope);
                        if (!groups.TryGetValue(key, out var accumulator))
                        {
                            accumulator = new Accumulator { ToPattern = toPattern };
                            groups[key] = accumulator;
                        }

                        accumulator.Count++;
                        if (accumulator.Sites.Count < maxSitesPerViolation)
                        {
                            var position = name.GetLocation().GetLineSpan();
                            accumulator.Sites.Add(new ViolationSite(
                                File: position.Path,
                                Line: position.StartLinePosition.Line + 1,
                                Column: position.StartLinePosition.Character + 1,
                                SourceSymbol: DescribeSource(name, model),
                                TargetSymbol: target.ToDisplayString()));
                        }
                    }
                }
            }
        }

        return groups
            .OrderBy(g => g.Key.RuleIndex)
            .ThenByDescending(g => g.Value.Count)
            .ThenBy(g => g.Key.Source, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Target, StringComparer.Ordinal)
            .Select(g => new ArchitectureViolation(
                RuleKind: rules[g.Key.RuleIndex].Kind,
                RuleDescription: rules[g.Key.RuleIndex].Description,
                FromPattern: rules[g.Key.RuleIndex].From,
                ToPattern: g.Value.ToPattern,
                SourceScope: g.Key.Source,
                TargetScope: g.Key.Target,
                ReferenceCount: g.Value.Count,
                Sites: g.Value.Sites))
            .ToList();
    }

    private static void Validate(IReadOnlyList<ArchitectureRule> rules, string scope)
    {
        if (!string.Equals(scope, NamespaceScope, StringComparison.Ordinal)
            && !string.Equals(scope, ProjectScope, StringComparison.Ordinal))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"Unknown scope '{scope}'. Expected '{NamespaceScope}' or '{ProjectScope}'.");
        }

        if (rules is null || rules.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                "At least one architecture rule is required.");
        }

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];

            if (!string.Equals(rule.Kind, ForbidKind, StringComparison.Ordinal)
                && !string.Equals(rule.Kind, AllowOnlyKind, StringComparison.Ordinal))
            {
                throw new McpToolException(ToolErrorCode.InvalidArgument,
                    $"rules[{i}]: unknown kind '{rule.Kind}'. Expected '{ForbidKind}' or '{AllowOnlyKind}'.",
                    new { ruleIndex = i });
            }

            if (rule.To is null || rule.To.Count == 0)
            {
                throw new McpToolException(ToolErrorCode.InvalidArgument,
                    $"rules[{i}]: '{rule.Kind}' requires a non-empty 'to'.",
                    new { ruleIndex = i });
            }

            if (!ScopePattern.IsValid(rule.From))
            {
                throw new McpToolException(ToolErrorCode.InvalidArgument,
                    $"rules[{i}]: malformed 'from' pattern '{rule.From}'. " +
                    "Use an exact scope, a prefix wildcard like 'Demo.Domain.*', or '*'.",
                    new { ruleIndex = i });
            }

            foreach (var pattern in rule.To)
            {
                if (!ScopePattern.IsValid(pattern))
                {
                    throw new McpToolException(ToolErrorCode.InvalidArgument,
                        $"rules[{i}]: malformed 'to' pattern '{pattern}'. " +
                        "Use an exact scope, a prefix wildcard like 'Demo.Domain.*', or '*'.",
                        new { ruleIndex = i });
                }
            }
        }
    }

    /// <summary>
    /// Every scope a tree could count as a source for. All namespace declarations (block and
    /// file-scoped, nested ones flattened), plus the global namespace when any member sits outside
    /// them — so a multi-namespace or partly-global file is never wrongly filtered out.
    /// </summary>
    private static IReadOnlyList<string> DeclaredScopes(SyntaxNode root)
    {
        var scopes = new List<string>();

        foreach (var declaration in root
                     .DescendantNodes(descendIntoChildren: n =>
                         n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                     .OfType<BaseNamespaceDeclarationSyntax>())
        {
            scopes.Add(FullNamespaceName(declaration));
        }

        if (scopes.Count == 0
            || (root is CompilationUnitSyntax unit
                && unit.Members.Any(m => m is not BaseNamespaceDeclarationSyntax)))
        {
            scopes.Add(string.Empty);
        }

        return scopes;
    }

    private static string FullNamespaceName(BaseNamespaceDeclarationSyntax declaration)
    {
        var parts = new List<string> { declaration.Name.ToString() };
        foreach (var outer in declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
            parts.Insert(0, outer.Name.ToString());
        return string.Join('.', parts);
    }

    private static string EnclosingNamespace(SyntaxNode node)
    {
        var declaration = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        return declaration is null ? string.Empty : FullNamespaceName(declaration);
    }

    private static string NamespaceOf(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString();
    }

    private static string TargetProject(ITypeSymbol type, SymbolResolver resolver)
    {
        var project = resolver.GetProjectName(type);
        return string.IsNullOrEmpty(project)
            ? type.ContainingAssembly?.Name ?? string.Empty
            : project;
    }

    /// <summary>
    /// The type a name node depends on, or null when the node expresses no type dependency.
    /// </summary>
    /// <remarks>
    /// <para><b>Why only <see cref="SimpleNameSyntax"/>, and why the qualifier check — the dedupe.</b>
    /// Resolving <i>every</i> descendant node double-counts: <c>Demo.Domain.Order</c> binds as a
    /// whole <c>QualifiedNameSyntax</c> AND again as its right-hand <c>Order</c> identifier, so one
    /// reference a human sees once would be counted twice (and three times once a member access is
    /// chained on). Restricting the walk to leaf name nodes counts each dotted name exactly once:
    /// the <c>Demo</c> and <c>Domain</c> parts bind to namespaces and drop out here, leaving only
    /// <c>Order</c>.</para>
    /// <para>The remaining duplicate is member access on a type — <c>Order.Zero</c> yields the type
    /// <c>Order</c> for the qualifier and, via <c>ContainingType</c>, <c>Order</c> again for
    /// <c>Zero</c>. When the qualifier already resolves to the same type, the member name is
    /// dropped. <c>local.Id</c> is unaffected: the qualifier is a local, not a type, so the member
    /// still counts — it is a genuine dependency on the field's declaring type.</para>
    /// <para>Measured on the <c>Grouping_CountsAllReferencesButLimitsSites</c> fixture, whose five
    /// cross-boundary references a human counts as five: the naive all-descendants walk reports 17;
    /// this one reports 5.</para>
    /// <para>Locals, parameters, range variables, labels and discards are skipped: a reference to a
    /// local is not a dependency on the type that happens to contain it.</para>
    /// <para><b><c>var</c> is skipped.</b> It is an <c>IdentifierNameSyntax</c> whose
    /// <c>GetSymbolInfo</c> returns the <i>inferred</i> type, so counting it reports a dependency
    /// the author never wrote and makes the count depend on whether the declaration was written
    /// explicitly. The intended count is <b>references a human reading the code would point at</b>:
    /// <c>var o = new Demo.Domain.Order(); return o.Id;</c> is two dependencies on <c>Order</c> —
    /// the construction and the member access — exactly what
    /// <c>Demo.Domain.Order o = new(); return o.Id;</c> yields. The check is syntactic, so a user
    /// type literally named <c>var</c> and referenced unqualified would also be skipped; that is
    /// accepted, since such a type cannot be written as a plain identifier without shadowing the
    /// contextual keyword anyway.</para>
    /// </remarks>
    private static ITypeSymbol? ResolveTargetType(SimpleNameSyntax name, SemanticModel model)
    {
        if (name is IdentifierNameSyntax { IsVar: true })
            return null;

        var symbol = model.GetSymbolInfo(name).Symbol;
        var target = TypeOf(symbol);
        if (target is null)
            return null;

        // Drop the trailing member of `SomeType.Member` — the qualifier already counted it.
        if (name.Parent is MemberAccessExpressionSyntax access
            && access.Name == name
            && symbol is not ITypeSymbol)
        {
            var qualifier = TypeOf(model.GetSymbolInfo(access.Expression).Symbol);
            if (qualifier is not null && SymbolEqualityComparer.Default.Equals(qualifier, target))
                return null;
        }

        return target;
    }

    private static ITypeSymbol? TypeOf(ISymbol? symbol)
    {
        var candidate = symbol switch
        {
            null => null,
            ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or ILabelSymbol or IDiscardSymbol => null,
            ITypeSymbol type => type,
            _ => symbol.ContainingType,
        };

        // Unwrap arrays/pointers so `Order[]` is a dependency on Order, not on a namespace-less
        // synthesized type.
        while (candidate is IArrayTypeSymbol array)
            candidate = array.ElementType;
        while (candidate is IPointerTypeSymbol pointer)
            candidate = pointer.PointedAtType;

        return candidate switch
        {
            null => null,
            ITypeParameterSymbol => null,               // T is not a dependency on anything
            { TypeKind: TypeKind.Error or TypeKind.Dynamic or TypeKind.Submission } => null,
            _ => candidate,
        };
    }

    /// <summary>
    /// True when every syntax tree that declares the type is generated code — nothing the author
    /// could edit. A partial type with one hand-written part is NOT generated-only: that part is
    /// editable, so a dependency on it is actionable.
    /// </summary>
    /// <remarks>
    /// Cached per symbol: the same target type is re-resolved at every reference site, and
    /// <see cref="GeneratedCodeDetector.IsGenerated"/> reads tree text.
    /// </remarks>
    private static bool IsGeneratedOnly(ITypeSymbol type, Dictionary<ISymbol, bool> cache)
    {
        if (cache.TryGetValue(type, out var known))
            return known;

        var declarations = type.DeclaringSyntaxReferences;
        var generated = declarations.Length > 0
            && declarations.All(r => GeneratedCodeDetector.IsGenerated(r.SyntaxTree));

        cache[type] = generated;
        return generated;
    }

    private static string DescribeSource(SyntaxNode node, SemanticModel model)
    {
        var member = node.Ancestors()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => m is not BaseNamespaceDeclarationSyntax);

        return member is null ? string.Empty : model.GetDeclaredSymbol(member)?.ToDisplayString() ?? string.Empty;
    }
}
