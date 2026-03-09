using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoslynCodeLens;

MSBuildLocator.RegisterDefaults();

var solutionPath = args.Length > 0
    ? args[0]
    : SolutionLoader.FindSolutionFile(Directory.GetCurrentDirectory());

SolutionManager manager;

if (solutionPath != null)
{
    manager = await SolutionManager.CreateAsync(solutionPath).ConfigureAwait(false);
}
else
{
    await Console.Error.WriteLineAsync("[roslyn-codelens] No .sln file found. Tools will return errors.").ConfigureAwait(false);
    manager = SolutionManager.CreateEmpty();
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton(manager);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);
