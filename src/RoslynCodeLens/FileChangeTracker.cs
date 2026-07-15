using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

public sealed class FileChangeTracker : IDisposable
{
    private readonly Dictionary<string, ProjectId> _fileToProject;
    private readonly Dictionary<ProjectId, List<ProjectId>> _reverseDeps;
    private readonly HashSet<ProjectId> _staleProjects = new();
    private readonly HashSet<string> _changedDocumentPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _requiresFullReload;
    private readonly FileSystemWatcher[] _watchers;
    private readonly Lock _lock = new();
    private Timer? _debounceTimer;
    private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] WatchedExtensions = [".cs", ".csproj", ".props", ".targets", ".dll"];

    /// <summary>Invoked (outside the lock) when a .dll file changes, so callers can invalidate PE caches.</summary>
    public Action<string>? OnDllChanged { get; set; }

    public FileChangeTracker(LoadedSolution loaded, string solutionPath)
    {
        _fileToProject = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        _reverseDeps = new Dictionary<ProjectId, List<ProjectId>>();
        PopulateMappings(loaded);

        // Start file watchers
        var fullSolutionPath = Path.GetFullPath(solutionPath);
        var solutionDir = Path.GetDirectoryName(fullSolutionPath)!;
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

    public IReadOnlySet<ProjectId> GetStaleProjectIds()
    {
        lock (_lock) return new HashSet<ProjectId>(_staleProjects);
    }

    /// <summary>
    /// A consistent snapshot of what has gone stale since the last drain: the transitively-closed
    /// set of stale projects, the source documents whose on-disk text changed (candidates for an
    /// in-place incremental rebuild via <c>Solution.WithDocumentText</c>), and whether any change
    /// is structural — a project file, an unknown/new file, etc. — and therefore needs a full
    /// solution reload rather than an incremental document edit.
    /// </summary>
    public readonly record struct StaleSnapshot(
        IReadOnlySet<ProjectId> StaleProjectIds,
        IReadOnlyList<string> ChangedDocumentPaths,
        bool RequiresFullReload);

    /// <summary>
    /// Atomically snapshots the current stale state <em>and clears it</em>, transferring ownership
    /// to the caller for the duration of a rebuild. Draining (rather than snapshot-then-clear-all
    /// afterwards) is what makes edits that arrive <em>during</em> a rebuild safe: they accumulate
    /// into the now-empty live sets instead of being wiped when the rebuild finishes. A rebuild can
    /// take tens of seconds on a large solution, so that window is real — clearing everything after
    /// the fact silently dropped any save made while it ran. On failure the caller must
    /// <see cref="RestoreStale"/> the returned snapshot so the work is retried, not lost.
    /// </summary>
    public StaleSnapshot DrainStale()
    {
        lock (_lock)
        {
            var snapshot = new StaleSnapshot(
                new HashSet<ProjectId>(_staleProjects),
                _changedDocumentPaths.ToList(),
                _requiresFullReload);
            _staleProjects.Clear();
            _changedDocumentPaths.Clear();
            _requiresFullReload = false;
            return snapshot;
        }
    }

    /// <summary>
    /// Merges a previously <see cref="DrainStale"/>d snapshot back into the live state (union with
    /// anything that arrived since), so a rebuild that failed re-arms and retries instead of
    /// silently dropping the changes it was meant to process.
    /// </summary>
    public void RestoreStale(StaleSnapshot snapshot)
    {
        lock (_lock)
        {
            _staleProjects.UnionWith(snapshot.StaleProjectIds);
            foreach (var path in snapshot.ChangedDocumentPaths)
                _changedDocumentPaths.Add(path);
            _requiresFullReload |= snapshot.RequiresFullReload;
        }
    }

    public void MarkProjectStale(ProjectId projectId)
    {
        lock (_lock)
        {
            MarkStaleTransitive(projectId);
        }
    }

    public ProjectId? FindProjectForFile(string filePath)
    {
        lock (_lock)
        {
            return _fileToProject.TryGetValue(filePath, out var id) ? id : null;
        }
    }

    public void ClearStale()
    {
        lock (_lock)
        {
            _staleProjects.Clear();
            _changedDocumentPaths.Clear();
            _requiresFullReload = false;
        }
    }

    /// <summary>
    /// Synchronously classifies a single changed path, bypassing the file watcher and its
    /// debounce timer. Test-only seam so the rebuild path can be exercised deterministically.
    /// </summary>
    internal void NotifyChangedPathForTest(string fullPath)
    {
        lock (_lock)
        {
            ClassifyChange(fullPath);
        }
    }

    public void UpdateMappings(LoadedSolution loaded)
    {
        lock (_lock)
        {
            _fileToProject.Clear();
            _reverseDeps.Clear();
            PopulateMappings(loaded);
        }
    }

    private void PopulateMappings(LoadedSolution loaded)
    {
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

    /// <summary>
    /// Classifies one changed path and records its effect. Must be called under <see cref="_lock"/>.
    /// A change to a known <c>.cs</c> document is incremental (its text can be re-applied in
    /// place). A change to a project file, or to any file we don't recognise as a document
    /// (a new source file, a <c>.props</c>/<c>.targets</c>, an MSBuild import), is structural
    /// and forces a full reload so MSBuild re-evaluates. <c>.dll</c> changes are handled
    /// separately via the PE-cache notification and are ignored here.
    /// </summary>
    private void ClassifyChange(string filePath)
    {
        if (filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return;

        if (_fileToProject.TryGetValue(filePath, out var projectId))
        {
            MarkStaleTransitive(projectId);
            if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                _changedDocumentPaths.Add(filePath);
            else
                _requiresFullReload = true; // project file edit — needs MSBuild re-evaluation
        }
        else
        {
            // Unknown file (new source file, props/targets, unmapped import) — mark all
            // projects stale and force a full reload.
            _requiresFullReload = true;
            foreach (var pid in _fileToProject.Values)
                _staleProjects.Add(pid);
        }
    }

    private void MarkStaleTransitive(ProjectId projectId)
    {
        if (!_staleProjects.Add(projectId))
            return;

        if (_reverseDeps.TryGetValue(projectId, out var dependents))
        {
            foreach (ref readonly var dep in CollectionsMarshal.AsSpan(dependents))
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
            changes = new HashSet<string>(_pendingChanges, StringComparer.OrdinalIgnoreCase);
            _pendingChanges.Clear();

            foreach (var filePath in changes)
                ClassifyChange(filePath);
        }

        // Notify DLL changes outside the lock
        var onDll = OnDllChanged;
        if (onDll != null)
        {
            foreach (var filePath in changes)
            {
                if (filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    onDll(filePath);
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
