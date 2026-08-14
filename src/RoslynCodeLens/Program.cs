using Microsoft.AspNetCore.Builder;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Security;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync($"[roslyn-codelens] {ex.Message}").ConfigureAwait(false);
    return 1;
}

var instance = MSBuildLocator.RegisterDefaults();
var dotnetSdkRoot = instance.MSBuildPath is not null
    ? Path.GetFullPath(Path.Combine(instance.MSBuildPath, "..", "..", ".."))
    : null;

MultiSolutionManager multiManager;

var solutionPaths = options.SolutionPaths.Count > 0
    ? options.SolutionPaths.ToList()
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

if (options.UseHttp)
    await RunHttpAsync().ConfigureAwait(false);
else
    await RunStdioAsync().ConfigureAwait(false);

return 0;

void RegisterServices(IServiceCollection services)
{
    services.AddSingleton(multiManager);
    services.AddSingleton(trustStore);
    services.AddSingleton(allowlist);
    services.AddSingleton<BackgroundTaskStore>();

    // Wrap every registered tool with StructuredErrorToolWrapper so thrown exceptions
    // surface as CallToolResult { IsError = true } carrying structured JSON.
    // OperationCanceledException intentionally bubbles unchanged.
    services.PostConfigure<McpServerOptions>(o =>
    {
        var coll = o.ToolCollection;
        if (coll is null) return;
        var wrapped = coll.Select(t => (McpServerTool)new StructuredErrorToolWrapper(t)).ToList();
        coll.Clear();
        foreach (var t in wrapped) coll.Add(t);
    });
}

async Task RunStdioAsync()
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    RegisterServices(builder.Services);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync().ConfigureAwait(false);
}

async Task RunHttpAsync()
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    RegisterServices(builder.Services);

    builder.Services
        .AddMcpServer()
        // Stateless streamable HTTP: no tool uses sampling/elicitation, so no
        // server-to-client channel (or per-session state) is needed.
        .WithHttpTransport(o => o.Stateless = true)
        .WithToolsFromAssembly();

    var app = builder.Build();
    app.MapMcp();

    var url = $"http://{options.HttpHost}:{options.Port}";
    if (!options.BindsLoopbackOnly)
        await Console.Error.WriteLineAsync(
            $"[roslyn-codelens] WARNING: binding to non-loopback address '{options.HttpHost}'. " +
            "This server has no authentication and its tools can read and modify source files; " +
            "only expose it on networks you fully trust.").ConfigureAwait(false);
    await Console.Error.WriteLineAsync($"[roslyn-codelens] HTTP transport listening on {url}").ConfigureAwait(false);

    await app.RunAsync(url).ConfigureAwait(false);
}
