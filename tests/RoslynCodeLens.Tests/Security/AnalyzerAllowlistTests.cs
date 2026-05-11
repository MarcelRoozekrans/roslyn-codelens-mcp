using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tests.Security;

public class AnalyzerAllowlistTests
{
    private static readonly string Nuget = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    [Fact]
    public void NugetGlobalPackages_IsAllowed_UnderDefaultPolicy()
    {
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
        var dll = Path.Combine(Nuget, "stylecop.analyzers", "1.2.0", "analyzers", "dotnet", "cs", "StyleCop.Analyzers.dll");
        Assert.True(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void SolutionBinPath_IsAllowed_UnderDefaultPolicy()
    {
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
        var dll = Path.Combine("c:\\repos\\my-app", "src", "MyProj", "bin", "Debug", "net8.0", "MyProj.Analyzers.dll");
        Assert.True(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void RandomPath_IsRejected_UnderDefaultPolicy()
    {
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
        Assert.False(policy.IsAllowed("c:\\evil\\Analyzer.dll", solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void SolutionLocalToolsPath_IsRejected_UnderDefaultPolicy()
    {
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
        var dll = Path.Combine("c:\\repos\\my-app", "tools", "EvilAnalyzer.dll");
        Assert.False(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void DotnetSdkPath_IsAllowed_WhenSdkRootKnown()
    {
        var sdkRoot = OperatingSystem.IsWindows() ? "c:\\Program Files\\dotnet" : "/usr/share/dotnet";
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: sdkRoot);
        var dll = Path.Combine(sdkRoot, "sdk", "10.0.100", "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll");
        Assert.True(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void AllPolicy_AllowsEverything()
    {
        var policy = new AnalyzerAllowlist("all", dotnetSdkRoot: null);
        Assert.True(policy.IsAllowed("c:\\evil\\Analyzer.dll", solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void StrictPolicy_RejectsSolutionBin()
    {
        var policy = new AnalyzerAllowlist("strict", dotnetSdkRoot: null);
        var dll = Path.Combine("c:\\repos\\my-app", "src", "MyProj", "bin", "Debug", "net8.0", "MyProj.Analyzers.dll");
        Assert.False(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }
}
