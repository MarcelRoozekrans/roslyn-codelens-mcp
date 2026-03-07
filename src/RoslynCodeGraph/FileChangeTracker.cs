using Microsoft.CodeAnalysis;

namespace RoslynCodeGraph;

public class FileChangeTracker : IDisposable
{
    private readonly Dictionary<string, ProjectId> _fileToProject;
    private readonly Dictionary<ProjectId, List<ProjectId>> _reverseDeps;
    private readonly HashSet<ProjectId> _staleProjects = new();
    private readonly FileSystemWatcher[] _watchers;
    private readonly object _lock = new();
    private Timer? _debounceTimer;
    private readonly HashSet<string> _pendingChanges = new();

    private static readonly string[] WatchedExtensions = [".cs", ".csproj", ".props", ".targets"];

    public FileChangeTracker(LoadedSolution loaded, string solutionPath)
    {
        // Build file-to-project mapping
        _fileToProject = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null)
                    _fileToProject[doc.FilePath] = project.Id;
            }
            if (project.FilePath != null)
                _fileToProject[project.FilePath] = project.Id;
        }

        // Build reverse dependency graph: if A references B, then B → [A]
        _reverseDeps = new Dictionary<ProjectId, List<ProjectId>>();
        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var projRef in project.ProjectReferences)
            {
                if (!_reverseDeps.TryGetValue(projRef.ProjectId, out var dependents))
                {
                    dependents = new List<ProjectId>();
                    _reverseDeps[projRef.ProjectId] = dependents;
                }
                dependents.Add(project.Id);
            }
        }

        // Start file watchers
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        _watchers = WatchedExtensions.Select(ext =>
        {
            var watcher = new FileSystemWatcher(solutionDir)
            {
                Filter = $"*{ext}",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += (s, e) =>
            {
                OnFileChanged(s, e);
                if (e.OldFullPath != null)
                    OnFileChangedPath(e.OldFullPath);
            };

            watcher.EnableRaisingEvents = true;
            return watcher;
        }).ToArray();
    }

    public bool HasStaleProjects
    {
        get { lock (_lock) return _staleProjects.Count > 0; }
    }

    public IReadOnlySet<ProjectId> StaleProjectIds
    {
        get { lock (_lock) return new HashSet<ProjectId>(_staleProjects); }
    }

    public ProjectId? FindProjectForFile(string filePath)
    {
        lock (_lock)
        {
            return _fileToProject.TryGetValue(filePath, out var id) ? id : null;
        }
    }

    public void MarkProjectStale(ProjectId projectId)
    {
        lock (_lock)
        {
            MarkStaleTransitive(projectId);
        }
    }

    public void ClearStale()
    {
        lock (_lock)
        {
            _staleProjects.Clear();
        }
    }

    public void UpdateMappings(LoadedSolution loaded)
    {
        lock (_lock)
        {
            _fileToProject.Clear();
            foreach (var project in loaded.Solution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    if (doc.FilePath != null)
                        _fileToProject[doc.FilePath] = project.Id;
                }
                if (project.FilePath != null)
                    _fileToProject[project.FilePath] = project.Id;
            }

            _reverseDeps.Clear();
            foreach (var project in loaded.Solution.Projects)
            {
                foreach (var projRef in project.ProjectReferences)
                {
                    if (!_reverseDeps.TryGetValue(projRef.ProjectId, out var dependents))
                    {
                        dependents = new List<ProjectId>();
                        _reverseDeps[projRef.ProjectId] = dependents;
                    }
                    dependents.Add(project.Id);
                }
            }
        }
    }

    private void MarkStaleTransitive(ProjectId projectId)
    {
        if (!_staleProjects.Add(projectId))
            return;

        if (_reverseDeps.TryGetValue(projectId, out var dependents))
        {
            foreach (var dep in dependents)
                MarkStaleTransitive(dep);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        OnFileChangedPath(e.FullPath);
    }

    private void OnFileChangedPath(string fullPath)
    {
        if (fullPath.Contains("/obj/") || fullPath.Contains("\\obj\\")
            || fullPath.Contains("/bin/") || fullPath.Contains("\\bin\\"))
            return;

        lock (_lock)
        {
            _pendingChanges.Add(fullPath);
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(ProcessPendingChanges, null, 200, Timeout.Infinite);
        }
    }

    private void ProcessPendingChanges(object? state)
    {
        HashSet<string> changes;
        lock (_lock)
        {
            changes = new HashSet<string>(_pendingChanges);
            _pendingChanges.Clear();
        }

        foreach (var filePath in changes)
        {
            var projectId = FindProjectForFile(filePath);
            if (projectId != null)
            {
                MarkProjectStale(projectId);
            }
            else
            {
                lock (_lock)
                {
                    foreach (var pid in _fileToProject.Values.Distinct())
                        _staleProjects.Add(pid);
                }
            }
        }
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        foreach (var watcher in _watchers)
            watcher.Dispose();
    }
}
