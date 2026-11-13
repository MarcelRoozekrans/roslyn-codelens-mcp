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
    /// The directory Roslyn puts generated documents under for this project: the configured
    /// generated-files directory, else the directory of the compilation output. Empty when neither
    /// is known, in which case Roslyn gives generated documents a relative path.
    /// </summary>
    internal static string GetGeneratedFilesBase(Project project)
    {
        var info = project.CompilationOutputInfo;
        return info.GeneratedFilesOutputDirectory
            ?? (info.AssemblyPath is { } assemblyPath ? Path.GetDirectoryName(assemblyPath) : null)
            ?? string.Empty;
    }

    /// <summary>
    /// Names the generator that produced a generated document. Roslyn builds the path as
    /// <c>&lt;base&gt;/&lt;assembly&gt;/&lt;type&gt;/&lt;hintName&gt;</c>, and both the base and the
    /// hint name may contain directories that spell some other generator's identity, so the identity
    /// is matched only at the position Roslyn puts it: right after <paramref name="baseDirectory"/>,
    /// or right before <paramref name="hintName"/>. Falls back to <see cref="InferFromPath"/> when
    /// no identity sits at either position.
    /// </summary>
    internal static string Resolve(
        IReadOnlyList<GeneratorIdentity> identities, string? filePath, string? hintName, string? baseDirectory)
    {
        if (string.IsNullOrEmpty(filePath))
            return InferFromPath(filePath, hintName);

        var path = Normalize(filePath);

        if (!string.IsNullOrEmpty(hintName))
        {
            var hint = Normalize(hintName);
            foreach (var id in identities)
            {
                if (("/" + path).EndsWith($"/{id.AssemblyName}/{id.TypeName}/{hint}", StringComparison.OrdinalIgnoreCase))
                    return id.TypeName;
            }
        }

        if (baseDirectory is not null)
        {
            var root = Normalize(baseDirectory);
            string? remainder = null;
            if (root.Length == 0)
                remainder = path;
            else if (path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                remainder = path[(root.Length + 1)..];

            if (remainder is not null)
            {
                foreach (var id in identities)
                {
                    if (remainder.StartsWith($"{id.AssemblyName}/{id.TypeName}/", StringComparison.OrdinalIgnoreCase))
                        return id.TypeName;
                }
            }
        }

        return InferFromPath(filePath, hintName);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

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
