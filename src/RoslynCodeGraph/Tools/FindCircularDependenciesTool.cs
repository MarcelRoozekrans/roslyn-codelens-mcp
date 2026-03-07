using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindCircularDependenciesLogic
{
    public static List<CircularDependency> Execute(LoadedSolution loaded, SymbolResolver resolver, string level)
    {
        return level.ToLowerInvariant() switch
        {
            "project" => FindProjectCycles(loaded),
            "namespace" => FindNamespaceCycles(loaded),
            _ => []
        };
    }

    private static List<CircularDependency> FindProjectCycles(LoadedSolution loaded)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in loaded.Solution.Projects)
        {
            var deps = new List<string>();
            foreach (var projRef in project.ProjectReferences)
            {
                var refProject = loaded.Solution.GetProject(projRef.ProjectId);
                if (refProject != null)
                    deps.Add(refProject.Name);
            }
            adjacency[project.Name] = deps;
        }

        return DetectCycles(adjacency, "project");
    }

    private static List<CircularDependency> FindNamespaceCycles(LoadedSolution loaded)
    {
        // Collect all namespaces defined in the solution
        var definedNamespaces = new HashSet<string>(StringComparer.Ordinal);
        // Map: namespace -> set of namespaces it depends on (via using directives)
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
                if (tree == null) continue;

                var root = tree.GetRoot();

                // Collect namespace declarations
                foreach (var nsDecl in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                {
                    var nsName = nsDecl.Name.ToString();
                    definedNamespaces.Add(nsName);
                }
            }
        }

        // Build adjacency from using directives within each namespace
        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
                if (tree == null) continue;

                var root = tree.GetRoot();

                // Get file-level usings and associate with namespaces in this file
                var fileUsings = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                    .Where(u => u.Parent is CompilationUnitSyntax)
                    .Select(u => u.Name?.ToString())
                    .Where(n => n != null && definedNamespaces.Contains(n))
                    .ToList();

                var namespacesInFile = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
                    .Select(ns => ns.Name.ToString())
                    .ToList();

                foreach (var ns in namespacesInFile)
                {
                    if (!adjacency.TryGetValue(ns, out var deps))
                    {
                        deps = new List<string>();
                        adjacency[ns] = deps;
                    }

                    // Add file-level usings as dependencies (exclude self-references)
                    foreach (var usingNs in fileUsings)
                    {
                        if (usingNs != null && !usingNs.Equals(ns, StringComparison.Ordinal))
                            deps.Add(usingNs);
                    }

                    // Add namespace-level usings
                    var nsDecl = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
                        .FirstOrDefault(n => n.Name.ToString() == ns);

                    if (nsDecl != null)
                    {
                        foreach (var usingDirective in nsDecl.DescendantNodes().OfType<UsingDirectiveSyntax>())
                        {
                            var usingName = usingDirective.Name?.ToString();
                            if (usingName != null && definedNamespaces.Contains(usingName) &&
                                !usingName.Equals(ns, StringComparison.Ordinal))
                            {
                                deps.Add(usingName);
                            }
                        }
                    }
                }
            }
        }

        // Deduplicate adjacency lists
        foreach (var key in adjacency.Keys)
        {
            adjacency[key] = adjacency[key].Distinct(StringComparer.Ordinal).ToList();
        }

        return DetectCycles(adjacency, "namespace");
    }

    private static List<CircularDependency> DetectCycles(Dictionary<string, List<string>> adjacency, string level)
    {
        var cycles = new List<CircularDependency>();
        var visited = new HashSet<string>(adjacency.Comparer);
        var onStack = new HashSet<string>(adjacency.Comparer);
        var stack = new List<string>();

        foreach (var node in adjacency.Keys)
        {
            if (!visited.Contains(node))
                Dfs(node, adjacency, visited, onStack, stack, cycles, level);
        }

        return cycles;
    }

    private static void Dfs(
        string node,
        Dictionary<string, List<string>> adjacency,
        HashSet<string> visited,
        HashSet<string> onStack,
        List<string> stack,
        List<CircularDependency> cycles,
        string level)
    {
        visited.Add(node);
        onStack.Add(node);
        stack.Add(node);

        if (adjacency.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!visited.Contains(neighbor))
                {
                    Dfs(neighbor, adjacency, visited, onStack, stack, cycles, level);
                }
                else if (onStack.Contains(neighbor))
                {
                    // Found a cycle - extract it from the stack
                    var cycleStart = stack.IndexOf(neighbor);
                    var cycle = stack.Skip(cycleStart).ToList();
                    cycle.Add(neighbor); // close the cycle
                    cycles.Add(new CircularDependency(level, cycle));
                }
            }
        }

        stack.RemoveAt(stack.Count - 1);
        onStack.Remove(node);
    }
}

[McpServerToolType]
public static class FindCircularDependenciesTool
{
    [McpServerTool(Name = "find_circular_dependencies"),
     Description("Detect circular dependencies in the project reference graph or namespace dependency graph")]
    public static List<CircularDependency> Execute(
        SolutionManager manager,
        [Description("Level: 'project' or 'namespace' (default: project)")] string level = "project")
    {
        manager.EnsureLoaded();
        return FindCircularDependenciesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), level);
    }
}
