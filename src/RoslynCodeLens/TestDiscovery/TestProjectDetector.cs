using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.TestDiscovery;

public static class TestProjectDetector
{
    private static readonly string[] TestPackagePrefixes = ["xunit", "nunit", "mstest"];

    // Matched against resolved metadata-reference assembly names. Covers xunit.core /
    // xunit.assert (v2) and xunit.v3.* (v3), nunit.framework / nunitlite, and MSTest —
    // whose framework assembly (Microsoft.VisualStudio.TestPlatform.TestFramework) does
    // not share the package's name, so it needs its own entry.
    private static readonly string[] TestAssemblyPrefixes =
        ["xunit", "nunit", "mstest", "Microsoft.VisualStudio.TestPlatform.TestFramework"];

    public static ImmutableHashSet<ProjectId> GetTestProjectIds(Solution solution)
    {
        var builder = ImmutableHashSet.CreateBuilder<ProjectId>();

        foreach (var project in solution.Projects)
        {
            if (HasTestFrameworkReference(project) || HasTestPackageReference(project))
                builder.Add(project.Id);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Primary signal: the project's resolved metadata references contain a test-framework
    /// assembly. Unlike the csproj text scan this follows MSBuild evaluation, so it sees
    /// packages declared in a Directory.Build.props, via Central Package Management, or through
    /// property indirection, as well as references arriving transitively (#406).
    /// </summary>
    private static bool HasTestFrameworkReference(Project project)
    {
        foreach (var reference in project.MetadataReferences)
        {
            var path = (reference as PortableExecutableReference)?.FilePath ?? reference.Display;
            if (path is not null && IsTestFrameworkAssembly(Path.GetFileNameWithoutExtension(path)))
                return true;
        }

        return false;
    }

    internal static bool IsTestFrameworkAssembly(string assemblyName)
    {
        foreach (var prefix in TestAssemblyPrefixes)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Fallback for degraded loads: when the design-time build dropped a project's metadata
    /// references (the #260/#263 flake), the csproj text still names any framework package the
    /// project declares directly.
    /// </summary>
    private static bool HasTestPackageReference(Project project)
    {
        if (project.FilePath is null || !File.Exists(project.FilePath))
            return false;

        var content = File.ReadAllText(project.FilePath);

        // Look for <PackageReference Include="xunit..." or "NUnit..." or "MSTest..."
        foreach (var prefix in TestPackagePrefixes)
        {
            var needle = $"PackageReference Include=\"{prefix}";
            if (content.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
