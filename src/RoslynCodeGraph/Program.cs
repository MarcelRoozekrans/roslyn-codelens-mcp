using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using RoslynCodeGraph;

MSBuildLocator.RegisterDefaults();

var solutionPath = args.Length > 0
    ? args[0]
    : SolutionLoader.FindSolutionFile(Directory.GetCurrentDirectory());

var loader = new SolutionLoader();
LoadedSolution loaded;

if (solutionPath != null)
{
    loaded = await loader.LoadAsync(solutionPath);
}
else
{
    Console.Error.WriteLine("[roslyn-codegraph] No .sln file found. Tools will return errors.");
    loaded = LoadedSolution.Empty;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(loaded);
builder.Services.AddSingleton<SymbolResolver>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
