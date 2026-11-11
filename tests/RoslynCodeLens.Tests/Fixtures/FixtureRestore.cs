using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Restores a fixture solution once, even when several test hosts start on a cold checkout.
///
/// A multi-targeted test project runs one test host per target framework, and the hosts share the
/// fixture folders on disk. Two hosts running <c>dotnet restore</c> on the same fixture race on its
/// <c>project.assets.json</c> and one of them fails. The hosts are separate processes, so the
/// restore is serialized with a lock file, and the assets are checked again once the lock is held.
/// </summary>
internal static class FixtureRestore
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);

    public static async Task EnsureAsync(string solutionPath, string label, Func<bool> isRestored)
    {
        if (isRestored())
            return;

        await using var gate = await AcquireAsync(solutionPath).ConfigureAwait(false);

        // Another host may have restored while this one waited for the lock.
        if (isRestored())
            return;

        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start 'dotnet restore' for the {label}.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(s_timeout);
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet restore' for the {label} failed (exit {process.ExitCode}):\n" +
                $"{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}");
        }
    }

    private static async Task<FileStream> AcquireAsync(string solutionPath)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(solutionPath)))[..16];
        var lockPath = Path.Combine(Path.GetTempPath(), $"roslyn-codelens-fixture-restore-{key}.lock");
        var deadline = DateTime.UtcNow + s_timeout;

        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
        }
    }
}
