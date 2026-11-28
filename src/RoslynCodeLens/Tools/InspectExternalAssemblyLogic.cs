using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class InspectExternalAssemblyLogic
{
    public static ExternalAssemblyOverview Execute(
        MetadataSymbolResolver metadata,
        string assemblyName,
        string mode,
        string? namespaceFilter)
    {
        var assembly = metadata.FindAssembly(assemblyName)
            ?? throw new ArgumentException(
                $"Assembly '{assemblyName}' is not referenced by any project in the solution. " +
                "Use get_nuget_dependencies to list referenced assemblies.",
                nameof(assemblyName));

        return mode switch
        {
            "summary" => BuildSummary(assembly),
            "namespace" => BuildNamespace(assembly, namespaceFilter
                ?? throw new ArgumentException("'namespaceFilter' is required when mode='namespace'.", nameof(namespaceFilter))),
            _ => throw new ArgumentException($"Unknown mode '{mode}'. Expected 'summary' or 'namespace'.", nameof(mode))
        };
    }

    private static ExternalAssemblyOverview BuildSummary(IAssemblySymbol assembly)
    {
        var byNamespace = new SortedDictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
        foreach (var type in SymbolResolver.GetAllTypes(assembly.GlobalNamespace))
        {
            if (type.DeclaredAccessibility != Accessibility.Public || type.ContainingType != null)
                continue;
            var ns = type.ContainingNamespace.ToDisplayString();
            if (!byNamespace.TryGetValue(ns, out var list))
                byNamespace[ns] = list = [];
            list.Add(type);
        }

        var tree = byNamespace.Select(kv => new ExternalNamespaceSummary(
            kv.Key, kv.Value.Count,
            kv.Value.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList()))
            .ToList();

        var id = assembly.Identity;
        return new ExternalAssemblyOverview(
            "summary", id.Name, id.Version.ToString(),
            TargetFramework: GetTargetFramework(assembly), PublicKeyToken: FormatPublicKey(id.PublicKeyToken),
            NamespaceTree: tree, Types: []);
    }

    private static ExternalAssemblyOverview BuildNamespace(IAssemblySymbol assembly, string ns)
    {
        var types = new List<INamedTypeSymbol>();
        foreach (var t in SymbolResolver.GetAllTypes(assembly.GlobalNamespace))
        {
            if (t.DeclaredAccessibility != Accessibility.Public || t.ContainingType != null)
                continue;
            if (!string.Equals(t.ContainingNamespace.ToDisplayString(), ns, StringComparison.Ordinal))
                continue;
            types.Add(t);
        }

        if (types.Count == 0)
            throw new ArgumentException(
                $"Namespace '{ns}' not found (or contains no public types) in assembly '{assembly.Identity.Name}'.",
                nameof(ns));

        var typeInfos = types.OrderBy(t => t.Name, StringComparer.Ordinal).Select(ToTypeInfo).ToList();

        var id = assembly.Identity;
        return new ExternalAssemblyOverview(
            "namespace", id.Name, id.Version.ToString(),
            TargetFramework: GetTargetFramework(assembly), PublicKeyToken: FormatPublicKey(id.PublicKeyToken),
            NamespaceTree: [], Types: typeInfos);
    }

    private static ExternalTypeInfo ToTypeInfo(INamedTypeSymbol t)
    {
        var kind = t.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => "class"
        };
        var modifiers = new List<string>();
        if (t.IsAbstract && !t.IsStatic && t.TypeKind != TypeKind.Interface) modifiers.Add("abstract");
        if (t.IsSealed && !t.IsStatic) modifiers.Add("sealed");
        if (t.IsStatic) modifiers.Add("static");

        var baseType = t.BaseType is { SpecialType: not SpecialType.System_Object }
            ? t.BaseType.ToDisplayString() : null;
        var interfaces = t.Interfaces.Select(i => i.ToDisplayString()).ToList();

        var isInterface = t.TypeKind == TypeKind.Interface;
        var members = new List<ExternalMemberInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Collect members from the type itself, then from base interfaces (for pure-marker
        // interfaces like IServiceCollection that define no own members but inherit from
        // IList<T>, ICollection<T>, etc.).
        var memberSources = isInterface
            ? new[] { t }.Concat(t.AllInterfaces.Cast<ITypeSymbol>())
            : new[] { (ITypeSymbol)t };

        foreach (var source in memberSources)
        {
            foreach (var m in source.GetMembers())
            {
                // Interface members are implicitly public; DeclaredAccessibility is NotApplicable.
                var accessible = m.DeclaredAccessibility == Accessibility.Public
                    || (isInterface && m.DeclaredAccessibility == Accessibility.NotApplicable);
                if (!accessible || m.IsImplicitlyDeclared)
                    continue;
                if (m is IMethodSymbol { MethodKind:
                    MethodKind.PropertyGet or MethodKind.PropertySet or
                    MethodKind.EventAdd or MethodKind.EventRemove or
                    MethodKind.EventRaise or MethodKind.StaticConstructor or
                    MethodKind.DelegateInvoke })
                    continue;
                var sig = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                if (!seen.Add(sig))
                    continue;
                members.Add(new ExternalMemberInfo(MemberKind(m), sig, SummaryFromXmlDoc(m)));
            }
        }

#pragma warning disable EPS06 // ImmutableArray<T> is a small struct; copy is intentional and cheap
        var attributes = t.GetAttributes()
            .Select(a => a.AttributeClass?.Name ?? "Attribute")
            .ToList();
#pragma warning restore EPS06

        return new ExternalTypeInfo(kind, t.ToDisplayString(), modifiers, baseType,
            interfaces, members, attributes, SummaryFromXmlDoc(t));
    }

    private static string MemberKind(ISymbol s) => s switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.Conversion or MethodKind.UserDefinedOperator } => "operator",
        IMethodSymbol { MethodKind: MethodKind.Destructor } => "destructor",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "symbol"
    };

    private static string? SummaryFromXmlDoc(ISymbol s)
    {
        var xml = s.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        var start = xml.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        if (start < 0 || end <= start)
            return null;
        return xml[(start + "<summary>".Length)..end].Trim();
    }

#pragma warning disable EPS06 // ImmutableArray<T> is a small struct; copy is intentional and cheap
    private static string? GetTargetFramework(IAssemblySymbol assembly)
    {
        var attr = assembly.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass is { Name: "TargetFrameworkAttribute" } ac &&
            string.Equals(
                ac.ContainingNamespace?.ToDisplayString(),
                "System.Runtime.Versioning",
                StringComparison.Ordinal));
        return attr?.ConstructorArguments.FirstOrDefault().Value as string;
    }
#pragma warning restore EPS06

#pragma warning disable EPS06 // ImmutableArray<byte> is a small struct; copy is intentional and cheap
    private static string? FormatPublicKey(System.Collections.Immutable.ImmutableArray<byte> key)
        => key.IsDefaultOrEmpty ? null : string.Concat(key.Select(b => b.ToString("x2")));
#pragma warning restore EPS06
}
