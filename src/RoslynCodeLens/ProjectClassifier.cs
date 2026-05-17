using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RoslynCodeLens;

internal static class ProjectClassifier
{
    public enum ProjectKind
    {
        SdkStyle,
        Legacy,
        Unknown,
        Missing,
    }

    public sealed record ClassifiedProject(string Path, string Name, ProjectKind Kind, string? Reason);

    public static IReadOnlyList<ClassifiedProject> EnumerateProjects(string solutionPath)
    {
        var ext = System.IO.Path.GetExtension(solutionPath);
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(solutionPath)) ?? "";

        IReadOnlyList<string> relativePaths;
        if (string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase))
            relativePaths = ParseSlnx(solutionPath);
        else
            relativePaths = ParseSln(solutionPath);

        var result = new List<ClassifiedProject>(relativePaths.Count);
        foreach (var rel in relativePaths)
        {
            var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, rel.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
            result.Add(ClassifyCsproj(abs));
        }
        return result;
    }

    private static IReadOnlyList<string> ParseSlnx(string slnxPath)
    {
        try
        {
            var doc = XDocument.Load(slnxPath);
            return doc.Descendants("Project")
                .Select(e => (string?)e.Attribute("Path"))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static readonly Regex SlnProjectLine = new(
        @"^Project\([""']\{[0-9A-Fa-f-]+\}[""']\)\s*=\s*[""']([^""']+)[""']\s*,\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static IReadOnlyList<string> ParseSln(string slnPath)
    {
        try
        {
            var content = File.ReadAllText(slnPath);
            var paths = new List<string>();
            foreach (Match m in SlnProjectLine.Matches(content))
            {
                var rel = m.Groups[2].Value;
                if (rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || rel.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                    || rel.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(rel);
                }
            }
            return paths;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static ClassifiedProject ClassifyCsproj(string absolutePath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(absolutePath);
        if (!File.Exists(absolutePath))
            return new ClassifiedProject(absolutePath, name, ProjectKind.Missing, "Project file not found on disk.");

        try
        {
            var doc = XDocument.Load(absolutePath);
            var root = doc.Root;
            if (root == null)
                return new ClassifiedProject(absolutePath, name, ProjectKind.Unknown, "Empty or malformed csproj.");

            if (root.Attribute("Sdk") != null)
                return new ClassifiedProject(absolutePath, name, ProjectKind.SdkStyle, null);

            var ns = root.Name.NamespaceName;
            if (string.Equals(ns, "http://schemas.microsoft.com/developer/msbuild/2003", StringComparison.OrdinalIgnoreCase))
                return new ClassifiedProject(absolutePath, name, ProjectKind.Legacy,
                    "Legacy (non-SDK-style) project; .NET Framework MSBuild format is not supported under the .NET SDK runtime.");

            return new ClassifiedProject(absolutePath, name, ProjectKind.Unknown,
                "Unrecognised project format (no Sdk attribute and no legacy MSBuild namespace).");
        }
        catch (Exception ex)
        {
            return new ClassifiedProject(absolutePath, name, ProjectKind.Unknown, $"Failed to read csproj: {ex.Message}");
        }
    }
}
