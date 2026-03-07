using System.ComponentModel;
using System.Xml.Linq;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetNugetDependenciesLogic
{
    public static NugetDependencyGraph? Execute(LoadedSolution loaded, string? project)
    {
        var packages = new List<NugetDependency>();

        foreach (var proj in loaded.Solution.Projects)
        {
            if (project != null &&
                !proj.Name.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            if (proj.FilePath == null)
                continue;

            var doc = XDocument.Load(proj.FilePath);
            foreach (var pkgRef in doc.Descendants("PackageReference"))
            {
                var name = pkgRef.Attribute("Include")?.Value;
                var version = pkgRef.Attribute("Version")?.Value
                    ?? pkgRef.Element("Version")?.Value
                    ?? "*";

                if (name != null)
                    packages.Add(new NugetDependency(name, version, proj.Name));
            }
        }

        return new NugetDependencyGraph(packages);
    }
}

[McpServerToolType]
public static class GetNugetDependenciesTool
{
    [McpServerTool(Name = "get_nuget_dependencies"),
     Description("List NuGet package references for projects in the solution")]
    public static NugetDependencyGraph? Execute(
        SolutionManager manager,
        [Description("Optional project name filter (omit to list all)")] string? project = null)
    {
        manager.EnsureLoaded();
        return GetNugetDependenciesLogic.Execute(manager.GetLoadedSolution(), project);
    }
}
