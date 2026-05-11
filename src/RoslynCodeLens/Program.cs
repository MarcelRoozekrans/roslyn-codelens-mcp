using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoslynCodeLens;

MSBuildLocator.RegisterDefaults();

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

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton(multiManager);
builder.Services.AddSingleton(new RoslynCodeLens.Security.TrustStore(RoslynCodeLens.Security.TrustStore.DefaultFilePath()));
builder.Services.AddSingleton(new RoslynCodeLens.Security.AnalyzerAllowlist(
    "nuget-and-solution-bin",
    RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(),
    dotnetSdkRoot: null));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);
