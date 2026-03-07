using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using RoslynCodeGraph;

MSBuildLocator.RegisterDefaults();

var solutionPath = args.Length > 0
    ? args[0]
    : SolutionLoader.FindSolutionFile(Directory.GetCurrentDirectory());

SolutionManager manager;

if (solutionPath != null)
{
    manager = await SolutionManager.CreateAsync(solutionPath);
}
else
{
    Console.Error.WriteLine("[roslyn-codegraph] No .sln file found. Tools will return errors.");
    manager = SolutionManager.CreateEmpty();
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(manager);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
