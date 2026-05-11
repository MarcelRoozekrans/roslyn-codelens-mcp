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
                if (normalized.StartsWith(normRoot, PathComparison)) return true;
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
        try
        {
            if (!File.Exists(_filePath)) return new TrustStoreModel();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<TrustStoreModel>(json, TrustStoreModel.JsonOptions) ?? new TrustStoreModel();
        }
        catch
        {
            return new TrustStoreModel();
        }
    }

    private void SaveToDisk()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_persistent, TrustStoreModel.JsonOptions));
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
