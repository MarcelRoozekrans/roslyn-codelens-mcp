using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetSourceGeneratorsLogic
{
    internal const string UnknownGenerator = "Unknown";

    /// <summary>
    /// Groups a project's real source-generator output by the generator that produced it.
    ///
    /// Uses <see cref="Project.GetSourceGeneratedDocumentsAsync"/> rather than sniffing syntax-tree
    /// paths for an <c>obj/</c> segment. The old path heuristic was wrong in both directions: it
    /// swept in MSBuild-authored intermediates that no generator produced (AssemblyInfo.cs,
    /// GlobalUsings.g.cs, .AssemblyAttributes.cs), and it could not name the generator, reporting
    /// "Unknown" — or, once real generators started running, whichever path segment happened to
    /// have no dot in it, e.g. "Components" for the Razor generator (issue #399).
    /// </summary>
    public static async Task<IReadOnlyList<SourceGeneratorInfo>> ExecuteAsync(
        LoadedSolution loaded, string? project, CancellationToken ct = default)
    {
        var results = new List<SourceGeneratorInfo>();

        foreach (var proj in loaded.Solution.Projects)
        {
            if (project != null && !proj.Name.Equals(project, StringComparison.OrdinalIgnoreCase))
                continue;

            var generated = await proj.GetSourceGeneratedDocumentsAsync(ct).ConfigureAwait(false);

            var byGenerator = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var document in generated)
            {
                var generatorName = InferGeneratorName(document.FilePath, document.HintName);
                if (!byGenerator.TryGetValue(generatorName, out var files))
                {
                    files = new List<string>();
                    byGenerator[generatorName] = files;
                }
                files.Add(document.FilePath ?? document.HintName);
            }

            foreach (var (generatorName, files) in byGenerator)
            {
                files.Sort(StringComparer.Ordinal);
                results.Add(new SourceGeneratorInfo(generatorName, proj.Name, files.Count, files));
            }
        }

        return results;
    }

    /// <summary>
    /// Recovers the generator's type name from the synthetic path Roslyn gives a source-generated
    /// document: <c>&lt;intermediateOutput&gt;/&lt;analyzerAssembly&gt;/&lt;generatorType&gt;/&lt;hintName&gt;</c>.
    /// The hint name may itself contain directory separators (the Razor generator uses the
    /// component's relative path), so it is stripped by length rather than by taking a fixed
    /// number of trailing segments.
    /// </summary>
    internal static string InferGeneratorName(string? filePath, string hintName)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(hintName))
            return UnknownGenerator;

        var path = filePath.Replace('\\', '/');
        var hint = hintName.Replace('\\', '/');
        if (!path.EndsWith(hint, StringComparison.OrdinalIgnoreCase))
            return UnknownGenerator;

        var directory = path[..^hint.Length].TrimEnd('/');
        var lastSeparator = directory.LastIndexOf('/');
        if (lastSeparator < 0 || lastSeparator == directory.Length - 1)
            return UnknownGenerator;

        return directory[(lastSeparator + 1)..];
    }
}
