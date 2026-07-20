using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Resolution, operation validation, cascade reporting and safety gates around
/// <see cref="ChangeSignatureBridge"/>. Nothing reflective lives here: the bridge owns Roslyn's
/// internal engine, this owns the contract the tool exposes.
/// </summary>
public static class ChangeSignatureLogic
{
    public static async Task<ChangeSignatureResult> ExecuteAsync(
        LoadedSolution loaded, SymbolResolver resolver,
        string method, IReadOnlyList<SignatureOperation> operations,
        bool preview, bool force,
        CommitWrittenDocuments? commitToMemory, CancellationToken ct)
    {
        if (operations == null || operations.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                "No operations supplied — pass at least one remove/reorder/add operation.",
                new { method });
        }

        var target = ResolveMethod(resolver, method);
        var document = GetDeclaringDocument(loaded, target, method);

        var oldSignature = FormatSignature(target.Name,
            target.Parameters.Select(p => p.ToDisplayString()));
        var resolvedName = target.ToDisplayString();

        var desired = BuildDesiredParameters(loaded, resolver, document, target, operations);
        var newSignature = FormatSignature(target.Name, desired.Select(DescribeParameter));

        var degradedRefusal = preview
            ? null
            : SolutionChangeSafety.DegradedApplyRefusal(loaded, force, "signature change");
        if (degradedRefusal != null)
        {
            return new ChangeSignatureResult(false, resolvedName, oldSignature, newSignature,
                Applied: false, [], 0, [], [], degradedRefusal);
        }

        var bridge = await ChangeSignatureBridge.ChangeSignatureAsync(
            document, target, desired, ct).ConfigureAwait(false);
        if (!bridge.Succeeded || bridge.UpdatedSolution == null)
        {
            // Roslyn's own verdict, surfaced verbatim. Nothing is written.
            return new ChangeSignatureResult(false, resolvedName, oldSignature, newSignature,
                Applied: false, [], 0, [], [],
                bridge.FailureMessage ?? "Roslyn refused the signature change.");
        }

        var updated = bridge.UpdatedSolution;
        var edits = await SolutionChangeWriter.ExtractTextEditsAsync(
            updated, loaded.Solution, ct).ConfigureAwait(false);
        var conflicts = await SolutionChangeSafety.ComputeConflictsAsync(
            loaded, updated, ct).ConfigureAwait(false);
        var cascadedTo = await FindCascadeSetAsync(loaded, target, ct).ConfigureAwait(false);
        var filesChanged = edits.Select(e => e.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (preview)
        {
            var previewMessage = conflicts.Count > 0
                ? $"{conflicts.Count} conflict(s) detected — applying would introduce new compiler errors."
                : "Preview only — no files written. Re-run with preview=false to apply.";
            previewMessage = SolutionChangeSafety.DegradedPreviewWarning(
                loaded, "signature change", previewMessage);
            return new ChangeSignatureResult(true, resolvedName, oldSignature, newSignature,
                Applied: false, edits, filesChanged, cascadedTo, conflicts, previewMessage);
        }

        if (conflicts.Count > 0 && !force)
        {
            return new ChangeSignatureResult(false, resolvedName, oldSignature, newSignature,
                Applied: false, edits, filesChanged, cascadedTo, conflicts,
                $"Refused to apply: {conflicts.Count} new compiler error(s) would be introduced. " +
                "Inspect Conflicts, or re-run with force=true to apply anyway.");
        }

        var write = await SolutionChangeWriter.WriteChangesToDiskAsync(
            updated, loaded.Solution, ct).ConfigureAwait(false);
        if (!write.Written)
        {
            // Something edited these files after the snapshot was taken — writing snapshot-derived
            // text would clobber those edits.
            return new ChangeSignatureResult(false, resolvedName, oldSignature, newSignature,
                Applied: false, edits, filesChanged, cascadedTo, conflicts,
                $"Refused to apply: {write.StaleFiles.Count} file(s) changed on disk after the solution " +
                $"snapshot was taken: {string.Join(", ", write.StaleFiles)}. No files were written. " +
                "Run rebuild_solution and retry.");
        }

        var message = $"Changed {resolvedName} to {newSignature} in {filesChanged} file(s).";
        if (cascadedTo.Count > 0)
            message += $" Cascaded to {cascadedTo.Count} override(s)/implementation(s).";

        // Post-write commit: the files are already changed on disk, so no outcome here —
        // cancellation included — may fail the operation.
        var commitWarning = await SolutionChangeWriter.CommitAsync(
            commitToMemory, write, ct).ConfigureAwait(false);
        if (commitWarning != null)
            message += " Warning: " + commitWarning;

        return new ChangeSignatureResult(true, resolvedName, oldSignature, newSignature,
            Applied: true, edits, filesChanged, cascadedTo, conflicts, message);
    }

    // ------------------------------------------------------------------ resolution

    internal static IMethodSymbol ResolveMethod(SymbolResolver resolver, string method)
    {
        var matches = resolver.FindSymbols(method);
        if (matches.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.SymbolNotFound,
                $"Method '{method}' not found.", new { method });
        }

        // Overloads share one logical group; unlike rename, change_signature edits exactly one
        // of them, so a group with more than one member is an ambiguity the caller must resolve.
        var groups = LogicalMemberGroups.GroupLogicalTargets(matches);
        if (groups.Count > 1)
        {
            throw new McpToolException(ToolErrorCode.AmbiguousMatch,
                $"'{method}' matched {groups.Count} distinct symbols: "
                    + string.Join(", ", groups.Select(g => g.First().ToDisplayString()))
                    + ". Use a more qualified name.",
                new { matches = groups.Select(g => g.First().ToDisplayString()).ToList() });
        }

        var group = groups[0].ToList();
        if (group.Count > 1)
        {
            var signatures = group.Select(s => s.ToDisplayString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
            throw new McpToolException(ToolErrorCode.AmbiguousMatch,
                $"'{method}' matched {group.Count} overloads: {string.Join(", ", signatures)}. "
                    + "change_signature edits one overload — qualify the call or rename it first.",
                new { overloads = signatures });
        }

        if (group[0] is not IMethodSymbol target)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{method}' is a {group[0].Kind} — change_signature only applies to methods.",
                new { method });
        }

        return target;
    }

    /// <summary>
    /// The document holding the declaration Roslyn will rewrite. A method with no source
    /// declaration (metadata) has none, and is refused here rather than at the bridge.
    /// </summary>
    private static Document GetDeclaringDocument(
        LoadedSolution loaded, IMethodSymbol target, string method)
    {
        foreach (var reference in target.DeclaringSyntaxReferences)
        {
            var document = loaded.Solution.GetDocument(reference.SyntaxTree);
            if (document != null)
                return document;
        }

        throw new McpToolException(ToolErrorCode.InvalidArgument,
            $"'{method}' has no declaration in source — only methods defined in source can have "
                + "their signature changed.",
            new { method });
    }

    // ------------------------------------------------------------------ operations

    private static List<DesiredParameter> BuildDesiredParameters(
        LoadedSolution loaded, SymbolResolver resolver, Document document,
        IMethodSymbol target, IReadOnlyList<SignatureOperation> operations)
    {
        var current = target.Parameters
            .Select(p => new DesiredParameter(p, p.Name, null, p.Type, null, null))
            .ToList();

        foreach (var operation in operations)
        {
            switch (operation.Kind?.ToLowerInvariant())
            {
                case "remove":
                    Remove(current, operation);
                    break;
                case "reorder":
                    current = Reorder(current, operation);
                    break;
                case "add":
                    current.Add(CreateAdded(loaded, resolver, document, current, operation));
                    break;
                default:
                    throw new McpToolException(ToolErrorCode.InvalidArgument,
                        $"Unknown operation kind '{operation.Kind}' — expected remove, reorder or add.",
                        new { kind = operation.Kind });
            }
        }

        ValidateExtensionReceiver(target, current);
        ValidateParamsIsLast(target, current);
        return current;
    }

    /// <summary>
    /// An extension method's receiver is identified BY POSITION: Roslyn's
    /// ParameterConfiguration.Create takes index 0 of the list as `this`. Moving another parameter
    /// into that slot, or dropping the original receiver, would therefore rebind `this` to a
    /// different parameter and silently change what every call site means.
    /// </summary>
    private static void ValidateExtensionReceiver(
        IMethodSymbol target, List<DesiredParameter> current)
    {
        if (!target.IsExtensionMethod || target.Parameters.Length == 0)
            return;

        var receiver = target.Parameters[0];
        if (current.Count > 0
            && SymbolEqualityComparer.Default.Equals(current[0].Existing, receiver))
        {
            return;
        }

        var stillPresent = current.Any(
            p => SymbolEqualityComparer.Default.Equals(p.Existing, receiver));
        var problem = stillPresent
            ? $"is no longer first (final order: {Describe(current)})"
            : "was removed";

        throw new McpToolException(ToolErrorCode.InvalidArgument,
            $"'{target.Name}' is an extension method: its receiver '{receiver.Name}' (the `this` "
                + $"parameter) {problem}. The receiver must stay first and cannot be removed — "
                + "Roslyn binds `this` by position, so moving it would silently rebind every call site.",
            new { receiver = receiver.Name, order = current.Select(p => p.Name).ToList() });
    }

    /// <summary>
    /// C# recognises `params` only on the LAST parameter (and Roslyn's engine re-derives it from
    /// the final slot), so a surviving params array anywhere else emits CS0231. Refused rather
    /// than written: an `add` appends, and a reorder can move it, so both reach here.
    /// </summary>
    private static void ValidateParamsIsLast(IMethodSymbol target, List<DesiredParameter> current)
    {
        var paramsParameter = target.Parameters.LastOrDefault(p => p.IsParams);
        if (paramsParameter == null)
            return;

        var index = current.FindIndex(
            p => SymbolEqualityComparer.Default.Equals(p.Existing, paramsParameter));
        if (index < 0 || index == current.Count - 1)
            return;   // removed, or still last — both fine

        throw new McpToolException(ToolErrorCode.InvalidArgument,
            $"'{paramsParameter.Name}' is a params array and must remain the last parameter, but "
                + $"the requested signature is ({Describe(current)}). Remove it, or place every "
                + "other parameter before it.",
            new { paramsParameter = paramsParameter.Name, order = current.Select(p => p.Name).ToList() });
    }

    private static void Remove(List<DesiredParameter> current, SignatureOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Parameter))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                "A remove operation requires 'parameter' — the name of the parameter to drop.",
                new { kind = operation.Kind });
        }

        var index = current.FindIndex(
            p => string.Equals(p.Name, operation.Parameter, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new McpToolException(ToolErrorCode.SymbolNotFound,
                $"Parameter '{operation.Parameter}' not found. Remaining parameters: "
                    + Describe(current) + ".",
                new { parameter = operation.Parameter, remaining = current.Select(p => p.Name).ToList() });
        }

        current.RemoveAt(index);
    }

    private static List<DesiredParameter> Reorder(
        List<DesiredParameter> current, SignatureOperation operation)
    {
        if (operation.Order == null || operation.Order.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                "A reorder operation requires 'order' — the full list of surviving parameter names.",
                new { kind = operation.Kind });
        }

        var order = operation.Order.ToList();
        var missing = current.Select(p => p.Name!)
            .Where(name => !order.Contains(name, StringComparer.Ordinal)).ToList();
        var unknown = order
            .Where(name => !current.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
            .ToList();
        var duplicated = order.GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (missing.Count > 0 || unknown.Count > 0 || duplicated.Count > 0)
        {
            var problems = new List<string>();
            if (missing.Count > 0)
                problems.Add("omits " + string.Join(", ", missing));
            if (unknown.Count > 0)
                problems.Add("names unknown parameter(s) " + string.Join(", ", unknown));
            if (duplicated.Count > 0)
                problems.Add("repeats " + string.Join(", ", duplicated));

            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"reorder must be a full permutation of the surviving parameters ({Describe(current)}) "
                    + "but " + string.Join("; ", problems) + ".",
                new { order, remaining = current.Select(p => p.Name).ToList() });
        }

        return order
            .Select(name => current.First(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
            .ToList();
    }

    private static DesiredParameter CreateAdded(
        LoadedSolution loaded, SymbolResolver resolver, Document document,
        List<DesiredParameter> current, SignatureOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Name))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                "An add operation requires 'name' — the new parameter's name.", new { kind = operation.Kind });
        }

        // Same guard rename_symbol uses: anything that isn't a lone valid identifier ("1st",
        // "x, int y", a keyword) would otherwise be written verbatim into the parameter list.
        if (!SyntaxFacts.IsValidIdentifier(operation.Name))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{operation.Name}' is not a valid C# identifier.", new { name = operation.Name });
        }

        // C# identifiers are case-sensitive, so the collision check is ordinal. Without it the
        // duplicate name silently aliases the original in reorder's name→parameter lookup.
        if (current.Any(p => string.Equals(p.Name, operation.Name, StringComparison.Ordinal)))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"A parameter named '{operation.Name}' already exists ({Describe(current)}) — "
                    + "parameter names must be unique.",
                new { name = operation.Name, existing = current.Select(p => p.Name).ToList() });
        }

        if (string.IsNullOrWhiteSpace(operation.Type))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"An add operation requires 'type' — the new parameter's type (adding '{operation.Name}').",
                new { name = operation.Name });
        }

        if (string.IsNullOrWhiteSpace(operation.CallSiteValue))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"An add operation requires 'callSiteValue' — the expression every existing call site "
                    + $"will pass for '{operation.Name}'. change_signature never guesses call-site semantics.",
                new { name = operation.Name });
        }

        var type = ResolveType(loaded, resolver, document, operation.Type);
        return new DesiredParameter(
            Existing: null,
            Name: operation.Name,
            TypeName: operation.Type,
            Type: type,
            CallSiteValue: operation.CallSiteValue,
            DefaultValue: operation.DefaultValue);
    }

    /// <summary>C# keyword aliases so `int` works as well as `System.Int32`.</summary>
    private static readonly Dictionary<string, SpecialType> KeywordAliases = new(StringComparer.Ordinal)
    {
        ["bool"] = SpecialType.System_Boolean,
        ["byte"] = SpecialType.System_Byte,
        ["sbyte"] = SpecialType.System_SByte,
        ["char"] = SpecialType.System_Char,
        ["decimal"] = SpecialType.System_Decimal,
        ["double"] = SpecialType.System_Double,
        ["float"] = SpecialType.System_Single,
        ["int"] = SpecialType.System_Int32,
        ["uint"] = SpecialType.System_UInt32,
        ["long"] = SpecialType.System_Int64,
        ["ulong"] = SpecialType.System_UInt64,
        ["short"] = SpecialType.System_Int16,
        ["ushort"] = SpecialType.System_UInt16,
        ["object"] = SpecialType.System_Object,
        ["string"] = SpecialType.System_String,
    };

    /// <summary>
    /// The added parameter's type, or a refusal. Both failure modes are explicit rather than
    /// degrading to null:
    /// <list type="bullet">
    /// <item>More than one match — binding to an arbitrary candidate would give the parameter a
    /// type the caller never named.</item>
    /// <item>No match — <c>CSharpChangeSignatureService.CreateNewParameterSyntax</c> calls
    /// <c>addedParameter.Type.GenerateTypeSyntax()</c> unconditionally, so a null type
    /// NullReferenceExceptions inside Roslyn (verified) instead of being written literally. The
    /// <c>typeBinds</c> flag does not gate that path.</item>
    /// </list>
    /// </summary>
    private static ITypeSymbol ResolveType(
        LoadedSolution loaded, SymbolResolver resolver, Document document, string typeName)
    {
        var compilation = document.Project.Id is { } projectId
            && loaded.Compilations.TryGetValue(projectId, out var cached)
                ? cached
                : null;

        if (compilation != null)
        {
            if (KeywordAliases.TryGetValue(typeName, out var special))
                return compilation.GetSpecialType(special);

            var byMetadataName = compilation.GetTypeByMetadataName(typeName);
            if (byMetadataName != null)
                return byMetadataName;
        }

        var matches = resolver.FindNamedTypes(typeName);
        if (matches.Count > 1)
        {
            var candidates = matches.Select(t => t.ToDisplayString())
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"Type '{typeName}' is ambiguous — it matches {candidates.Count} types: "
                    + string.Join(", ", candidates) + ". Use the fully qualified name.",
                new { type = typeName, candidates });
        }

        if (matches.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"Type '{typeName}' could not be resolved in this solution. Use the fully "
                    + "qualified name, or add the type first — an unresolved type cannot be "
                    + "written into a signature.",
                new { type = typeName });
        }

        return matches[0];
    }

    // ------------------------------------------------------------------ cascade

    /// <summary>
    /// Overrides and interface implementations Roslyn's engine rewrites alongside the target, so
    /// the blast radius is visible before applying.
    /// <para>
    /// The walk goes UP before it goes down. Roslyn cascades over the entire hierarchy, so
    /// enumerating only what the target itself is overridden/implemented by under-reports
    /// whenever the target is not the root: changing a derived override also rewrites the base
    /// and every sibling override, and changing one interface implementation also rewrites the
    /// interface member and every other implementer. The roots are found first
    /// (<see cref="FindHierarchyRoots"/>), then the downward enumeration runs from each.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> FindCascadeSetAsync(
        LoadedSolution loaded, IMethodSymbol target, CancellationToken ct)
    {
        var cascade = new List<ISymbol>();
        foreach (var root in FindHierarchyRoots(target))
        {
            // The root itself is rewritten too, unless it IS the target.
            if (!SymbolEqualityComparer.Default.Equals(root, target))
                cascade.Add(root);

            cascade.AddRange(await SymbolFinder.FindOverridesAsync(
                root, loaded.Solution, cancellationToken: ct).ConfigureAwait(false));
            cascade.AddRange(await SymbolFinder.FindImplementationsAsync(
                root, loaded.Solution, cancellationToken: ct).ConfigureAwait(false));
        }

        return cascade
            .Where(s => !SymbolEqualityComparer.Default.Equals(s, target))
            .Select(s => s.ToDisplayString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The base-most declaration(s) the target participates in: the top of its override chain,
    /// plus every interface member it implements (explicitly or implicitly). The target itself
    /// is the only root when it heads its own hierarchy.
    /// </summary>
    private static List<IMethodSymbol> FindHierarchyRoots(IMethodSymbol target)
    {
        var roots = new List<IMethodSymbol>();

        var topOfChain = target;
        while (topOfChain.OverriddenMethod is { } overridden)
            topOfChain = overridden;
        roots.Add(topOfChain);

        // Explicit implementations name their interface members directly.
        roots.AddRange(target.ExplicitInterfaceImplementations);

        // Implicit ones must be matched through the containing type: an interface member whose
        // implementation resolves to the target (or to the base it overrides) shares the cascade.
        var containingType = target.ContainingType;
        if (containingType != null)
        {
            foreach (var @interface in containingType.AllInterfaces)
            {
                foreach (var member in @interface.GetMembers(target.Name).OfType<IMethodSymbol>())
                {
                    var implementation = containingType.FindImplementationForInterfaceMember(member);
                    if (implementation != null
                        && (SymbolEqualityComparer.Default.Equals(implementation, target)
                            || SymbolEqualityComparer.Default.Equals(implementation, topOfChain)))
                    {
                        roots.Add(member);
                    }
                }
            }
        }

        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        return roots.Where(r => seen.Add(r)).ToList();
    }

    // ------------------------------------------------------------------ display

    private static string Describe(IEnumerable<DesiredParameter> parameters)
    {
        var names = parameters.Select(p => p.Name).ToList();
        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }

    private static string FormatSignature(string name, IEnumerable<string> parameters)
        => $"{name}({string.Join(", ", parameters)})";

    private static string DescribeParameter(DesiredParameter parameter)
    {
        if (parameter.Existing != null)
            return parameter.Existing.ToDisplayString();

        var text = $"{parameter.Type?.ToDisplayString() ?? parameter.TypeName} {parameter.Name}";
        return string.IsNullOrEmpty(parameter.DefaultValue)
            ? text
            : $"{text} = {parameter.DefaultValue}";
    }
}
