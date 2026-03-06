using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using RoslynCodeGraph;

MSBuildLocator.RegisterDefaults();

var solutionPath = args.Length > 0
    ? args[0]
    : SolutionLoader.FindSolutionFile(Directory.GetCurrentDirectory());

if (solutionPath == null)
{
    Console.Error.WriteLine("[roslyn-codegraph] No .sln file found. Tools will return errors.");
}

var loader = new SolutionLoader();
LoadedSolution? loaded = null;

if (solutionPath != null)
{
    loaded = await loader.LoadAsync(solutionPath);
}

var builder = Host.CreateApplicationBuilder(args);

if (loaded != null)
{
    builder.Services.AddSingleton(loaded);
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
