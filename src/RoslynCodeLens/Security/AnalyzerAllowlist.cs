namespace RoslynCodeLens.Security;

public sealed class AnalyzerAllowlist
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _policy;
    private readonly string _nugetGlobal;
    private readonly string? _dotnetSdkRoot;

    public AnalyzerAllowlist(string policy, string nugetGlobal, string? dotnetSdkRoot)
    {
        _policy = policy;
        _nugetGlobal = nugetGlobal;
        _dotnetSdkRoot = dotnetSdkRoot;
    }

    public static string DefaultNugetGlobal() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    public bool IsAllowed(string analyzerDllPath, string solutionDir)
    {
        if (string.Equals(_policy, "all", StringComparison.OrdinalIgnoreCase))
            return true;

        var full = Path.GetFullPath(analyzerDllPath);

        if (StartsWith(full, _nugetGlobal)) return true;
        if (_dotnetSdkRoot is not null && StartsWith(full, _dotnetSdkRoot)) return true;

        if (string.Equals(_policy, "strict", StringComparison.OrdinalIgnoreCase))
            return false;

        // nuget-and-solution-bin (default): also accept solution-local bin/obj
        var binRoot = Path.GetFullPath(solutionDir);
        if (StartsWith(full, binRoot))
        {
            var rel = full.Substring(binRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var segments = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(s => string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static bool StartsWith(string path, string prefix)
    {
        var p = Path.GetFullPath(prefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        var full = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        return path.StartsWith(p, PathComparison) || full.StartsWith(p, PathComparison);
    }
}
