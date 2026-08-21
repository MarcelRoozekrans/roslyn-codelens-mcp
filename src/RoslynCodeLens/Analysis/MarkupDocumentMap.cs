using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// Maps between markup files (.razor/.cshtml) and the C# documents a source generator produces
/// from them.
///
/// Markup is an <c>AdditionalDocument</c>, so the only C# that exists for it lives in generator
/// output under <c>obj/</c>. Every symbol, reference and diagnostic for a component therefore
/// reports a path the user never wrote and cannot edit. Carrying the authored file alongside is
/// what makes those answers actionable — "NavMenu is unused" is not useful until it says the file
/// to delete is <c>Components/NavMenu.razor</c>.
///
/// Line numbers deliberately are not mapped: the Razor generator emits <c>#line</c> directives
/// only for user-written C# (<c>@code</c> blocks, expressions), not for the scaffolding a component
/// tag compiles into. A <c>&lt;NavMenu /&gt;</c> usage has <c>HasMappedPath == false</c>, so there
/// is no authored line to recover and inventing one would be worse than omitting it.
/// </summary>
public static class MarkupDocumentMap
{
    private static readonly string[] MarkupExtensions = [".razor", ".cshtml"];

    public static bool IsMarkupPath(string? path) =>
        path is not null &&
        MarkupExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The hint name the Razor generator gives a component's output: the markup file's
    /// project-relative path with the extension folded into the name.
    /// <c>Components/Pages/Counter.razor</c> becomes <c>Components/Pages/Counter_razor.g.cs</c>.
    /// </summary>
    public static string ExpectedHintName(TextDocument additional)
    {
        var fileName = additional.Name.Replace('.', '_') + ".g.cs";
        return additional.Folders.Count == 0
            ? fileName
            : string.Join('/', additional.Folders) + "/" + fileName;
    }

    /// <summary>
    /// Generated document path to the markup file it was produced from, across the solution.
    ///
    /// Matched by hint-name suffix against the compilation's syntax trees rather than by calling
    /// <c>GetSourceGeneratedDocumentsAsync</c>, so this stays synchronous and can be built during
    /// <see cref="SymbolResolver"/> construction alongside the other indexes.
    /// </summary>
    public static Dictionary<string, string> BuildGeneratedToMarkup(LoadedSolution loaded)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in loaded.Solution.Projects)
        {
            var markupDocuments = project.AdditionalDocuments
                .Where(d => IsMarkupPath(d.FilePath))
                .ToList();
            if (markupDocuments.Count == 0)
                continue;

            if (!loaded.Compilations.TryGetValue(project.Id, out var compilation))
                continue;

            // Only generator output can match a hint name, and in a markup project that set is
            // roughly the markup count — so the pairwise scan below stays small.
            var generatedTrees = compilation.SyntaxTrees
                .Where(t => !string.IsNullOrEmpty(t.FilePath) && t.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (generatedTrees.Count == 0)
                continue;

            foreach (var markup in markupDocuments)
            {
                var hint = Normalize(ExpectedHintName(markup));
                foreach (var tree in generatedTrees)
                {
                    if (!Normalize(tree.FilePath).EndsWith(hint, StringComparison.OrdinalIgnoreCase))
                        continue;
                    map[tree.FilePath] = markup.FilePath!;
                    break;
                }
            }
        }

        return map;
    }

    // Roslyn builds a generated document's path from a directory portion using the platform
    // separator and a hint name that keeps '/', so both forms can appear in one path.
    private static string Normalize(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
