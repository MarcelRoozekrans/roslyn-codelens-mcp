using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoslynCodeLens;
using RoslynCodeLens.Security;

var instance = MSBuildLocator.RegisterDefaults();
var dotnetSdkRoot = instance.MSBuildPath is not null
    ? Path.GetFullPath(Path.Combine(instance.MSBuildPath, "..", "..", ".."))
    : null;

MultiSolutionManager multiManager;

var solutionPaths = args.Length > 0
    ? args.ToList()
    : SolutionLoader.FindSolutionFile(Directory.GetCurrentDirectory()) is { } found
        ? [found]
        : [];

if (solutionPaths.Count > 0)
{
    multiManager = await MultiSolutionManager.CreateAsync(solutionPaths).ConfigureAwait(false);
}
else
{
    await Console.Error.WriteLineAsync("[roslyn-codelens] No .sln file found. Tools will return errors.").ConfigureAwait(false);
    multiManager = MultiSolutionManager.CreateEmpty();
}

var trustStore = new TrustStore(TrustStore.DefaultFilePath());
foreach (var sln in solutionPaths)
    trustStore.AddSessionTrust(Path.GetFullPath(sln));

var allowlist = new AnalyzerAllowlist(trustStore.AnalyzerPolicy, AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton(multiManager);
builder.Services.AddSingleton(trustStore);
builder.Services.AddSingleton(allowlist);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);
