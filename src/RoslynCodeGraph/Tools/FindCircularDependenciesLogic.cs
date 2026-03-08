using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindCircularDependenciesLogic
{
    public static IReadOnlyList<CircularDependency> Execute(LoadedSolution loaded, SymbolResolver resolver, string level)
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
        var definedNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Single pass: collect namespaces and build adjacency from syntax trees
        foreach (var (_, compilation) in loaded.Compilations)
        {
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var root = syntaxTree.GetRoot();

                // Collect file-level usings
                var fileUsings = new List<string>();
                foreach (var u in root.ChildNodes().OfType<UsingDirectiveSyntax>())
                {
                    var name = u.Name?.ToString();
                    if (name != null)
                        fileUsings.Add(name);
                }

                // Process each namespace declaration
                foreach (var nsDecl in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                {
                    var nsName = nsDecl.Name.ToString();
                    definedNamespaces.Add(nsName);

                    if (!adjacency.TryGetValue(nsName, out var deps))
                    {
                        deps = new HashSet<string>(StringComparer.Ordinal);
                        adjacency[nsName] = deps;
                    }

                    // Add file-level usings (will filter to defined namespaces later)
                    foreach (var u in fileUsings.Where(u => !string.Equals(u, nsName, StringComparison.Ordinal)))
                    {
                        deps.Add(u);
                    }

                    // Add namespace-level usings
                    foreach (var usingDirective in nsDecl.ChildNodes().OfType<UsingDirectiveSyntax>())
                    {
                        var usingName = usingDirective.Name?.ToString();
                        if (usingName != null && !string.Equals(usingName, nsName, StringComparison.Ordinal))
                            deps.Add(usingName);
                    }
                }
            }
        }

        // Filter adjacency to only include defined namespaces
        var filtered = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (ns, deps) in adjacency)
        {
            filtered[ns] = deps.Where(d => definedNamespaces.Contains(d)).ToList();
        }

        return DetectCycles(filtered, "namespace");
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
            foreach (var neighbor in CollectionsMarshal.AsSpan(neighbors))
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
