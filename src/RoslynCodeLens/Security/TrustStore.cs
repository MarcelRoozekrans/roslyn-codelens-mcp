using System.Text.Json;

namespace RoslynCodeLens.Security;

public sealed class TrustStore
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly HashSet<string> _sessionSolutions;
    private TrustStoreModel _persistent;

    public TrustStore(string filePath)
    {
        _filePath = filePath;
        _sessionSolutions = new HashSet<string>(PathComparer);
        _persistent = LoadFromDisk();
    }

    public static string DefaultFilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "roslyn-codelens", "trust.json");

    public bool IsTrusted(string solutionPath)
    {
        var normalized = Normalize(solutionPath);
        lock (_lock)
        {
            if (_sessionSolutions.Contains(normalized)) return true;
            if (_persistent.TrustedSolutions.Any(t => Normalize(t.Path).Equals(normalized, PathComparison))) return true;
            foreach (var root in _persistent.TrustedRoots)
            {
                var normRoot = Normalize(root);
                if (normalized.Equals(normRoot, PathComparison)) return true;
                var rootWithSep = normRoot.EndsWith(Path.DirectorySeparatorChar) ? normRoot : normRoot + Path.DirectorySeparatorChar;
                if (normalized.StartsWith(rootWithSep, PathComparison)) return true;
            }
            return false;
        }
    }

    public void AddSessionTrust(string solutionPath)
    {
        lock (_lock) _sessionSolutions.Add(Normalize(solutionPath));
    }

    public void AddPersistentTrust(string solutionPath)
    {
        lock (_lock)
        {
            var norm = Normalize(solutionPath);
            if (!_persistent.TrustedSolutions.Any(t => Normalize(t.Path).Equals(norm, PathComparison)))
                _persistent.TrustedSolutions.Add(new TrustedSolution(solutionPath, DateTimeOffset.UtcNow));
            SaveToDisk();
        }
    }

    public void AddTrustedRoot(string rootPath)
    {
        lock (_lock)
        {
            var norm = Normalize(rootPath);
            if (!_persistent.TrustedRoots.Any(r => Normalize(r).Equals(norm, PathComparison)))
                _persistent.TrustedRoots.Add(rootPath);
            SaveToDisk();
        }
    }

    public void Revoke(string solutionPath)
    {
        lock (_lock)
        {
            var norm = Normalize(solutionPath);
            _sessionSolutions.Remove(norm);
            _persistent.TrustedSolutions.RemoveAll(t => Normalize(t.Path).Equals(norm, PathComparison));
            _persistent.TrustedRoots.RemoveAll(r => Normalize(r).Equals(norm, PathComparison));
            SaveToDisk();
        }
    }

    public string AnalyzerPolicy
    {
        get { lock (_lock) return _persistent.AnalyzerPolicy; }
    }

    public TrustSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new TrustSnapshot(
                _sessionSolutions.ToList(),
                _persistent.TrustedSolutions.ToList(),
                _persistent.TrustedRoots.ToList(),
                _persistent.AnalyzerPolicy);
        }
    }

    private TrustStoreModel LoadFromDisk()
    {
        if (!File.Exists(_filePath)) return new TrustStoreModel();
        try
        {
            var json = File.ReadAllText(_filePath);
            var model = JsonSerializer.Deserialize<TrustStoreModel>(json, TrustStoreModel.JsonOptions);
            if (model is null)
            {
                Console.Error.WriteLine($"[roslyn-codelens] trust.json could not be loaded (deserialized to null); treating as empty.");
                return new TrustStoreModel();
            }
            return model;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[roslyn-codelens] trust.json could not be loaded ({ex.GetType().Name}: {ex.Message}); treating as empty.");
            return new TrustStoreModel();
        }
    }

    /// <summary>
    /// Writes the store to a temp file and renames it over the target, so a crash cannot leave a
    /// half-written trust.json.
    /// <para>
    /// The temp name carries a GUID rather than a fixed <c>.tmp</c> suffix. Two saves in quick
    /// succession — which <see cref="AddPersistentTrust"/> does whenever a caller trusts twice —
    /// would otherwise recreate the exact path just renamed away, and on Windows that path can
    /// still be held briefly by a scanner or indexer. A fresh name per save cannot collide with
    /// the previous one.
    /// </para>
    /// <para>
    /// Both file operations go through <see cref="RunWithRetry"/>: those holds are transient, and
    /// failing the whole call because an antivirus looked at the file for a few milliseconds means
    /// the server silently fails to persist trust the user just granted.
    /// </para>
    /// </summary>
    private void SaveToDisk()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(_persistent, TrustStoreModel.JsonOptions);
        try
        {
            RunWithRetry(() => File.WriteAllText(tmp, json));
            RunWithRetry(() => File.Move(tmp, _filePath, overwrite: true));
        }
        catch
        {
            // A GUID-named temp would otherwise be left behind for good, since nothing else knows
            // its name. Best-effort: the original exception is what the caller needs to see.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Retries a file operation through the transient sharing violations Windows produces when a
    /// scanner, indexer or backup agent momentarily holds a file that was just written. Waits
    /// grow linearly (10ms, 20ms, ...), which is ample for a hold measured in milliseconds and
    /// still bounded well under a tenth of a second in the worst case.
    /// <para>
    /// The final attempt does NOT catch: a genuine permission problem — a read-only file, a
    /// locked-down profile directory — must surface as itself rather than as a timeout, and
    /// retrying it five times changes nothing but the delay.
    /// </para>
    /// </summary>
    internal static void RunWithRetry(Action operation, int maxAttempts = 5, int baseDelayMs = 10)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                Thread.Sleep(baseDelayMs * attempt);
            }
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static IEqualityComparer<string> PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public sealed record TrustSnapshot(
    IReadOnlyList<string> SessionSolutions,
    IReadOnlyList<TrustedSolution> PersistentSolutions,
    IReadOnlyList<string> TrustedRoots,
    string AnalyzerPolicy);
