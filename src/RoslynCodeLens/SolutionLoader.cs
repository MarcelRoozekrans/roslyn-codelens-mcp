using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeLens;

public class SolutionLoader
{
    private const int DefaultOpenProjectTimeoutSec = 300;

    internal static int GetOpenProjectTimeoutSec() =>
        int.TryParse(
            Environment.GetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS"),
            out var n) && n > 0
                ? n
                : DefaultOpenProjectTimeoutSec;

    internal static async Task<T?> RunWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T?>> work,
        int timeoutSec,
        CancellationToken outerCt) where T : class
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        try
        {
            return await work(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            // User cancellation — surface a plain OperationCanceledException so callers
            // can distinguish internal timeout (null return) from user-requested cancellation.
            throw new OperationCanceledException(outerCt);
        }
    }

    public async Task<(Solution Solution, MSBuildWorkspace Workspace, IReadOnlyList<SkippedProject> Skipped)> OpenAsync(string solutionPath, ProjectFilter? filter = null, CancellationToken ct = default)
    {
        var classified = ProjectClassifier.EnumerateProjects(solutionPath);

        if (filter is not null && filter.HasSeeds)
        {
            return await OpenFilteredAsync(solutionPath, classified, filter, ct).ConfigureAwait(false);
        }

        var legacyOrMissing = classified
            .Where(p => p.Kind is ProjectClassifier.ProjectKind.Legacy
                     or ProjectClassifier.ProjectKind.Missing
                     or ProjectClassifier.ProjectKind.Unknown)
            .ToList();

        var preFilter = legacyOrMissing.Any(p => p.Kind == ProjectClassifier.ProjectKind.Legacy);
        if (preFilter)
        {
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Detected {legacyOrMissing.Count(p => p.Kind == ProjectClassifier.ProjectKind.Legacy)} legacy non-SDK project(s) in {Path.GetFileName(solutionPath)}; loading SDK-style projects only.")
                .ConfigureAwait(false);
            return await OpenPerProjectAsync(solutionPath, classified, ct).ConfigureAwait(false);
        }

        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            Console.Error.WriteLine($"[roslyn-codelens] Warning: {e.Diagnostic.Message}");
        };

        await Console.Error.WriteLineAsync($"[roslyn-codelens] Loading solution: {Path.GetFileName(solutionPath)}").ConfigureAwait(false);

        Solution? solution;
        try
        {
            solution = await RunWithTimeoutAsync<Solution>(
                innerCt => workspace.OpenSolutionAsync(solutionPath, cancellationToken: innerCt),
                GetOpenProjectTimeoutSec(),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fallback: solution-level open threw (often a legacy project we did not classify, or
            // an SDK project with a broken import). Reuse per-project loading so we still surface
            // whatever can be loaded plus a list of what was skipped.
            workspace.Dispose();
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Solution-level load failed ({ex.GetType().Name}: {ex.Message}); falling back to per-project loading.")
                .ConfigureAwait(false);
            return await OpenPerProjectAsync(solutionPath, classified, ct).ConfigureAwait(false);
        }

        if (solution is null)
        {
            workspace.Dispose();
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Solution-level load timed out; falling back to per-project loading.")
                .ConfigureAwait(false);
            return await OpenPerProjectAsync(solutionPath, classified, ct).ConfigureAwait(false);
        }

        return (solution, workspace, Array.Empty<SkippedProject>());
    }

    private static async Task<(Solution Solution, MSBuildWorkspace Workspace, IReadOnlyList<SkippedProject> Skipped)> OpenPerProjectAsync(
        string solutionPath,
        IReadOnlyList<ProjectClassifier.ClassifiedProject> classified,
        CancellationToken ct)
    {
        var workspace = MSBuildWorkspace.Create();
        var skipped = new List<SkippedProject>();

        workspace.WorkspaceFailed += (_, e) =>
        {
            Console.Error.WriteLine($"[roslyn-codelens] Warning: {e.Diagnostic.Message}");
        };

        foreach (var entry in classified)
        {
            if (entry.Kind == ProjectClassifier.ProjectKind.SdkStyle)
            {
                // Opening a project pulls in its transitive ProjectReferences, so a
                // later entry may already be present. Skip it rather than letting
                // OpenProjectAsync throw "already part of the workspace" (which would
                // otherwise be misreported as a load failure).
                if (workspace.CurrentSolution.Projects.Any(p =>
                        string.Equals(p.FilePath, entry.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var timeoutSec = GetOpenProjectTimeoutSec();
                Project? loaded;
                try
                {
                    loaded = await RunWithTimeoutAsync<Project>(
                        innerCt => workspace.OpenProjectAsync(entry.Path, cancellationToken: innerCt),
                        timeoutSec,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"[roslyn-codelens] Skipping project {entry.Name}: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
                    skipped.Add(new SkippedProject(entry.Path, entry.Name, "Failed",
                        $"{ex.GetType().Name}: {ex.Message}"));
                    continue;
                }

                if (loaded is null)
                {
                    await Console.Error.WriteLineAsync(
                        $"[roslyn-codelens] Timeout loading project {entry.Name} (exceeded {timeoutSec}s).").ConfigureAwait(false);
                    skipped.Add(new SkippedProject(entry.Path, entry.Name, "Timeout",
                        $"Project load exceeded {timeoutSec}s. " +
                        $"Set ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS to override."));
                }
            }
            else
            {
                var kind = entry.Kind.ToString();
                var reason = entry.Reason ?? "Unsupported project format.";
                await Console.Error.WriteLineAsync(
                    $"[roslyn-codelens] Skipping {kind} project {entry.Name}: {reason}").ConfigureAwait(false);
                skipped.Add(new SkippedProject(entry.Path, entry.Name, kind, reason));
            }
        }

        return (workspace.CurrentSolution, workspace, skipped);
    }

    private static async Task<(Solution Solution, MSBuildWorkspace Workspace, IReadOnlyList<SkippedProject> Skipped)> OpenFilteredAsync(
        string solutionPath,
        IReadOnlyList<ProjectClassifier.ClassifiedProject> classified,
        ProjectFilter filter,
        CancellationToken ct)
    {
        // Build a name -> referenced-project-names graph. References are read as
        // absolute paths and resolved back to project names via the classified set,
        // so only references that point at projects in this solution form edges.
        var nameByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in classified)
            nameByPath[entry.Path] = entry.Name;

        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in classified)
        {
            if (entry.Kind != ProjectClassifier.ProjectKind.SdkStyle)
            {
                graph[entry.Name] = Array.Empty<string>();
                continue;
            }

            var refPaths = ProjectGraphReader.ReadProjectReferences(entry.Path);
            var refNames = new List<string>(refPaths.Count);
            foreach (var refPath in refPaths)
            {
                if (nameByPath.TryGetValue(refPath, out var refName))
                    refNames.Add(refName);
            }
            graph[entry.Name] = refNames;
        }

        // May throw InvalidOperationException for empty seeds / unknown roots /
        // invalid globs; callers surface that as a load error.
        var closure = ProjectClosure.Compute(filter, classified.Select(p => p.Name), graph);

        await Console.Error.WriteLineAsync(
            $"[roslyn-codelens] Loading {closure.Loaded.Count}/{classified.Count} project(s) from {Path.GetFileName(solutionPath)} (filtered).")
            .ConfigureAwait(false);

        // Projects excluded by the filter are reported up front; the remaining
        // (in-closure) projects are loaded per-project, reusing the exact same
        // loading/timeout/skip logic as the unfiltered fallback path.
        var inClosure = new List<ProjectClassifier.ClassifiedProject>(closure.Loaded.Count);
        var filteredOut = new List<SkippedProject>();
        foreach (var entry in classified)
        {
            if (closure.Loaded.Contains(entry.Name))
                inClosure.Add(entry);
            else
                filteredOut.Add(new SkippedProject(entry.Path, entry.Name, "FilteredOut",
                    "Excluded by load_solution filter."));
        }

        var (solution, workspace, perProjectSkipped) =
            await OpenPerProjectAsync(solutionPath, inClosure, ct).ConfigureAwait(false);

        var skipped = new List<SkippedProject>(filteredOut.Count + perProjectSkipped.Count);
        skipped.AddRange(filteredOut);
        skipped.AddRange(perProjectSkipped);

        return (solution, workspace, skipped);
    }

    public async Task<ConcurrentDictionary<ProjectId, Compilation>> CompileAllParallelAsync(Solution solution)
    {
        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();
        var levels = GetCompilationLevels(solution);
        var totalProjects = levels.Sum(l => l.Count);
        var compiled = 0;

#pragma warning disable HLQ012, ZA0601 // async method — cannot use CollectionsMarshal.AsSpan across await; Select in loop is required for parallel task projection
        foreach (var level in levels)
        {
            var tasks = level.Select(async project =>
            {
                var index = Interlocked.Increment(ref compiled);
                await Console.Error.WriteLineAsync(
                    $"[roslyn-codelens] Compiling project {index}/{totalProjects}: {project.Name}").ConfigureAwait(false);

                var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
                if (compilation != null)
                {
                    compilations[project.Id] = compilation;
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
#pragma warning restore HLQ012, ZA0601

        return compilations;
    }

    public async Task<LoadedSolution> LoadAsync(string solutionPath)
    {
        var (solution, _, skipped) = await OpenAsync(solutionPath).ConfigureAwait(false);
        var compilations = await CompileAllParallelAsync(solution).ConfigureAwait(false);

        await Console.Error.WriteLineAsync(
            $"[roslyn-codelens] Ready. {compilations.Count} projects compiled, {skipped.Count} skipped.").ConfigureAwait(false);

        return new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations,
            SkippedProjects = skipped
        };
    }

    public static string? FindSolutionFile(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            FileInfo? shortest = null;
            foreach (var f in dir.GetFiles("*.sln"))
            {
                if (shortest == null || f.FullName.Length < shortest.FullName.Length)
                    shortest = f;
            }
            foreach (var f in dir.GetFiles("*.slnx"))
            {
                if (shortest == null || f.FullName.Length < shortest.FullName.Length)
                    shortest = f;
            }
            if (shortest != null)
                return shortest.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Returns projects grouped by dependency level (leaves first).
    /// Level 0 = no project dependencies, Level 1 = depends only on level 0, etc.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Project>> GetCompilationLevels(Solution solution)
    {
        var projects = solution.Projects.ToList();
        var projectIds = new HashSet<ProjectId>(projects.Select(p => p.Id));
        var assigned = new Dictionary<ProjectId, int>();
        var visiting = new HashSet<ProjectId>();

        int GetLevel(Project project)
        {
            if (assigned.TryGetValue(project.Id, out var cached))
                return cached;

            if (!visiting.Add(project.Id))
                return 0;

            var maxDep = -1;
            foreach (var dep in project.ProjectReferences)
            {
                if (!projectIds.Contains(dep.ProjectId))
                    continue;
                var depProject = solution.GetProject(dep.ProjectId);
                if (depProject != null)
                    maxDep = Math.Max(maxDep, GetLevel(depProject));
            }

            var level = maxDep + 1;
            assigned[project.Id] = level;
            visiting.Remove(project.Id);
            return level;
        }

        foreach (ref readonly var project in CollectionsMarshal.AsSpan(projects))
            GetLevel(project);

        if (assigned.Count == 0)
            return [];

        var maxLevel = assigned.Values.Max();
        var levels = new List<List<Project>>(maxLevel + 1);
        for (var i = 0; i <= maxLevel; i++)
            levels.Add(new List<Project>());

        foreach (ref readonly var project in CollectionsMarshal.AsSpan(projects))
            levels[assigned[project.Id]].Add(project);

        return levels;
    }
}
