using System.Xml;

namespace RoslynCodeLens;

/// <summary>
/// Reads <c>&lt;ProjectReference Include="…"&gt;</c> targets from a single
/// <c>.csproj</c> via a lightweight XML pass. Does not perform MSBuild
/// evaluation, so it stays under 1ms per file even on large solutions.
/// Malformed files return an empty edge list — callers treat that as
/// "no edges"; the project itself will surface its parse failure later
/// when <c>MSBuildWorkspace</c> tries to open it.
/// </summary>
public static class ProjectGraphReader
{
    public static IReadOnlyList<string> ReadProjectReferences(string projectPath)
    {
        if (!File.Exists(projectPath)) return Array.Empty<string>();

        var dir = Path.GetDirectoryName(projectPath)!;
        var results = new List<string>();

        try
        {
            using var reader = XmlReader.Create(projectPath, new XmlReaderSettings { IgnoreComments = true });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (!string.Equals(reader.LocalName, "ProjectReference", StringComparison.Ordinal)) continue;

                var include = reader.GetAttribute("Include");
                if (string.IsNullOrWhiteSpace(include)) continue;

                // ProjectReference Include paths are authored with Windows-style
                // backslash separators (the form MSBuild emits). Normalise them to
                // the OS separator before combining, otherwise on non-Windows the
                // backslashes are treated as ordinary characters: the ".." segment
                // never collapses, the path matches no project, and the dependency
                // edge is silently dropped — degenerating the transitive closure to
                // the seed set. Mirrors ProjectClassifier.EnumerateProjects.
                var normalisedInclude = include.Replace('\\', Path.DirectorySeparatorChar);
                var absolute = Path.GetFullPath(Path.Combine(dir, normalisedInclude));
                results.Add(absolute);
            }
        }
        catch (XmlException)
        {
            return Array.Empty<string>();
        }

        return results;
    }
}
