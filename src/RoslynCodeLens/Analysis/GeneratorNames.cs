using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// Names the source generator that produced a generated file. Shared by get_source_generators and
/// get_generated_code so the two tools always agree (issue #549).
/// </summary>
internal static class GeneratorNames
{
    internal const string Unknown = "Unknown";

    /// <summary>
    /// The generators a project's analyzer references expose for its language. The references
    /// cache their generator instances and the compilation already ran them, so this executes no
    /// extra generator code.
    /// </summary>
    internal static IReadOnlyList<GeneratorIdentity> GetIdentities(Project project)
    {
        var identities = new List<GeneratorIdentity>();
        foreach (var reference in project.AnalyzerReferences)
        {
            foreach (var generator in reference.GetGenerators(project.Language))
            {
                var type = generator.GetGeneratorType();
                var assembly = type.Assembly.GetName().Name;
                if (assembly is not null)
                    identities.Add(new GeneratorIdentity(assembly, type.FullName ?? type.Name));
            }
        }
        return identities;
    }

    /// <summary>
    /// Matches the file path against <c>/&lt;assembly&gt;/&lt;type&gt;/</c> for each known
    /// generator, longest type name first so a generator whose name is a prefix of another does not
    /// shadow it. Falls back to <see cref="InferFromPath"/> when no identity matches.
    /// </summary>
    internal static string Resolve(
        IReadOnlyList<GeneratorIdentity> identities, string? filePath, string? hintName)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            var path = filePath.Replace('\\', '/');
            foreach (var id in identities.OrderByDescending(i => i.TypeName.Length))
            {
                if (path.Contains($"/{id.AssemblyName}/{id.TypeName}/", StringComparison.OrdinalIgnoreCase))
                    return id.TypeName;
            }
        }
        return InferFromPath(filePath, hintName);
    }

    /// <summary>
    /// Recovers the generator's type name from the synthetic path Roslyn gives a source-generated
    /// document: <c>&lt;intermediateOutput&gt;/&lt;analyzerAssembly&gt;/&lt;generatorType&gt;/&lt;hintName&gt;</c>.
    /// The hint name may itself contain directory separators (the Razor generator uses the
    /// component's relative path), so it is stripped by length rather than by taking a fixed
    /// number of trailing segments. Needs the hint name; without it the answer is Unknown.
    /// </summary>
    internal static string InferFromPath(string? filePath, string? hintName)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(hintName))
            return Unknown;

        var path = filePath.Replace('\\', '/');
        var hint = hintName.Replace('\\', '/');
        if (!path.EndsWith(hint, StringComparison.OrdinalIgnoreCase))
            return Unknown;

        var directory = path[..^hint.Length].TrimEnd('/');
        var lastSeparator = directory.LastIndexOf('/');
        if (lastSeparator < 0 || lastSeparator == directory.Length - 1)
            return Unknown;

        return directory[(lastSeparator + 1)..];
    }
}
