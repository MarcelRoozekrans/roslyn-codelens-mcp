using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
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

// Wrap every registered tool with StructuredErrorToolWrapper so thrown exceptions
// surface as CallToolResult { IsError = true } carrying structured JSON.
// OperationCanceledException intentionally bubbles unchanged.
builder.Services.PostConfigure<McpServerOptions>(options =>
{
    var coll = options.ToolCollection;
    if (coll is null) return;
    var wrapped = coll.Select(t => (McpServerTool)new StructuredErrorToolWrapper(t)).ToList();
    coll.Clear();
    foreach (var t in wrapped) coll.Add(t);
});

await builder.Build().RunAsync().ConfigureAwait(false);
