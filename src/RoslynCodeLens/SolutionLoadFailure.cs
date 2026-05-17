namespace RoslynCodeLens;

internal static class SolutionLoadFailure
{
    public static string Describe(string solutionPath, Exception ex)
    {
        var fileName = Path.GetFileName(solutionPath);

        if (LooksLikeLegacyMsBuildFailure(ex))
        {
            return $"Failed to load solution '{fileName}': it appears to contain non-SDK-style " +
                   "(legacy .NET Framework) projects, which MSBuildWorkspace cannot load when " +
                   "running under the .NET SDK. Convert affected projects to SDK-style csproj, " +
                   "or run the server from a Visual Studio Developer environment. " +
                   $"Underlying error: {ex.GetType().Name}: {ex.Message}";
        }

        return $"Failed to load solution '{fileName}': {ex.GetType().Name}: {ex.Message}";
    }

    private static bool LooksLikeLegacyMsBuildFailure(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is FileNotFoundException && MentionsLegacyMsBuildArtifact(current.Message))
                return true;

            if (current.GetType().FullName == "Microsoft.Build.Exceptions.InvalidProjectFileException"
                && MentionsLegacyMsBuildArtifact(current.Message))
                return true;
        }
        return false;
    }

    private static bool MentionsLegacyMsBuildArtifact(string message)
    {
        return message.Contains("Microsoft.Common.props", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Microsoft.Common.targets", StringComparison.OrdinalIgnoreCase)
            || message.Contains("MSBuildExtensionsPath", StringComparison.OrdinalIgnoreCase)
            || message.Contains("MSBuildToolsVersion", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ToolsVersion", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TargetFrameworkVersion", StringComparison.OrdinalIgnoreCase);
    }
}
