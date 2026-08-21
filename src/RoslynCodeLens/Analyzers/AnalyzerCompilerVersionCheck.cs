using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynCodeLens.Analyzers;

/// <summary>
/// Detects analyzer/source-generator assemblies that Roslyn will silently refuse to load because
/// they were built against a NEWER Microsoft.CodeAnalysis than the one this process runs
/// (<c>AnalyzerLoadFailed</c> with <c>ReferencesNewerCompiler</c>).
///
/// This matters because the refusal is invisible: <c>GetGenerators</c> returns an empty list, the
/// generator never contributes source, and every downstream answer is confidently wrong rather
/// than merely incomplete. Issue #399 was exactly this — the .NET SDK's Razor generator targets a
/// newer Roslyn than the one packaged here, so a Blazor solution that builds clean reported three
/// phantom compile errors and had no symbols for any <c>.razor</c>-only component.
///
/// The SDK moves its Roslyn every feature band (10.0.1xx→5.0, 10.0.2xx→5.3, 10.0.3xx→5.5,
/// 10.0.4xx→5.9), so a NuGet-distributed tool can never stay permanently ahead. Keeping the
/// package current is necessary but not sufficient; reporting the skew is what keeps a lagging
/// build honest instead of wrong.
///
/// The check reads PE metadata only — no assembly is loaded, so it is cheap and cannot itself
/// fail on a skewed assembly.
/// </summary>
public static class AnalyzerCompilerVersionCheck
{
    private const string RoslynAssemblyName = "Microsoft.CodeAnalysis";

    // Analyzer sets are shared across projects (every project in a solution typically references
    // the same ~20 SDK analyzers), and the answer is a property of the file on disk.
    private static readonly ConcurrentDictionary<string, Version?> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Microsoft.CodeAnalysis version this process is running.</summary>
    public static Version RunningCompilerVersion { get; } =
        typeof(Compilation).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// The Microsoft.CodeAnalysis version <paramref name="analyzerPath"/> was compiled against, or
    /// null when the file is unreadable, is not a managed assembly, or does not reference Roslyn
    /// at all (a resource-only or utility assembly shipped alongside the real analyzer).
    /// </summary>
    public static Version? ReadRequiredCompilerVersion(string analyzerPath) =>
        s_cache.GetOrAdd(analyzerPath, static path =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                    return null;

                var metadata = peReader.GetMetadataReader();
                foreach (var handle in metadata.AssemblyReferences)
                {
                    var reference = metadata.GetAssemblyReference(handle);
                    if (metadata.GetString(reference.Name).Equals(RoslynAssemblyName, StringComparison.Ordinal))
                        return reference.Version;
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                // Unreadable or native — nothing to report; Roslyn will ignore it too.
                return null;
            }
        });

    /// <summary>
    /// Load diagnostics for every analyzer reference in <paramref name="solution"/> that Roslyn
    /// will skip because it targets a newer compiler. One message per distinct analyzer assembly,
    /// naming how many projects it affects — a large solution references the same skewed SDK
    /// analyzer from every project and a per-project message would bury the signal.
    /// </summary>
    /// <param name="runningVersion">
    /// Host compiler version to compare against; defaults to <see cref="RunningCompilerVersion"/>.
    /// Injectable so tests can exercise the skewed branch without a mismatched assembly on disk.
    /// </param>
    public static IReadOnlyList<string> FindSkewedAnalyzers(Solution solution, Version? runningVersion = null)
    {
        var running = runningVersion ?? RunningCompilerVersion;
        Dictionary<string, (Version Required, int Projects)>? skewed = null;

        foreach (var project in solution.Projects)
        {
            foreach (var reference in project.AnalyzerReferences)
            {
                if (reference is not AnalyzerFileReference fileReference)
                    continue;

                var required = ReadRequiredCompilerVersion(fileReference.FullPath);
                if (required is null || required <= running)
                    continue;

                var name = Path.GetFileNameWithoutExtension(fileReference.FullPath);
                skewed ??= new Dictionary<string, (Version, int)>(StringComparer.OrdinalIgnoreCase);
                skewed[name] = skewed.TryGetValue(name, out var existing)
                    ? (required, existing.Projects + 1)
                    : (required, 1);
            }
        }

        if (skewed is null)
            return Array.Empty<string>();

        var messages = new List<string>(skewed.Count);
        foreach (var (name, (required, projects)) in skewed)
        {
            messages.Add(
                $"{name}: built against Roslyn {required} but roslyn-codelens runs {running} — Roslyn " +
                $"skips it silently (ReferencesNewerCompiler), affecting {projects} project(s). Any source " +
                "it generates is missing from the compilation, so diagnostics, symbols and references for " +
                "generated code will be wrong. Upgrade roslyn-codelens, or pin an older .NET SDK via global.json.");
        }

        messages.Sort(StringComparer.Ordinal);
        return messages;
    }
}
