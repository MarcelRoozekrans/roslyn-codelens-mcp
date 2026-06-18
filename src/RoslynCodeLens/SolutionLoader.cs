using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynCodeLens;

public class SolutionLoader
{
    private const int DefaultOpenProjectTimeoutSec = 300;
    private const int MaxDefaultParallelism = 8;

    internal static int GetOpenProjectTimeoutSec() =>
        int.TryParse(
            Environment.GetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS"),
            out var n) && n > 0
                ? n
                : DefaultOpenProjectTimeoutSec;

    // Each worker opens projects in its own MSBuildWorkspace, which spins up a
    // separate out-of-process BuildHost. That is the only way to get genuine
    // build parallelism (one workspace serialises its builds through one
    // BuildHost), but each process costs memory — so the fan-out is bounded.
    // Override with ROSLYN_CODELENS_LOAD_PARALLELISM; 1 forces single-worker
    // loading (deterministic, used by tests and as an escape hatch).
    internal static int GetLoadParallelism() =>
        int.TryParse(
            Environment.GetEnvironmentVariable("ROSLYN_CODELENS_LOAD_PARALLELISM"),
            out var n) && n > 0
                ? n
                : Math.Max(1, Math.Min(Environment.ProcessorCount, MaxDefaultParallelism));

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

    public async Task<(Solution Solution, Workspace Workspace, IReadOnlyList<SkippedProject> Skipped)> OpenAsync(string solutionPath, ProjectFilter? filter = null, CancellationToken ct = default)
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

    private static async Task<(Solution Solution, Workspace Workspace, IReadOnlyList<SkippedProject> Skipped)> OpenPerProjectAsync(
        string solutionPath,
        IReadOnlyList<ProjectClassifier.ClassifiedProject> classified,
        CancellationToken ct)
    {
        var skipped = new List<SkippedProject>();

        // Non-SDK entries (legacy/missing/unknown) are never opened; report them
        // up front, exactly as the old sequential loader did.
        var targets = new List<ProjectClassifier.ClassifiedProject>(classified.Count);
        foreach (var entry in classified)
        {
            if (entry.Kind == ProjectClassifier.ProjectKind.SdkStyle)
            {
                targets.Add(entry);
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

        if (targets.Count == 0)
        {
            return (new AdhocWorkspace().CurrentSolution, new AdhocWorkspace(), skipped);
        }

        // On-disk ProjectReference graph, restricted to the target set. Used both
        // to order roots-first (so a few big transitive opens populate the cache
        // and the leaves are skipped before they are scheduled) and to re-wire
        // references after re-stitch. Reuses the same lightweight XML reader and
        // separator normalisation as the filter feature, so it is cross-platform.
        var targetPaths = new HashSet<string>(targets.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            var refs = new List<string>();
            foreach (var refPath in ProjectGraphReader.ReadProjectReferences(t.Path))
                if (targetPaths.Contains(refPath))
                    refs.Add(refPath);
            graph[t.Path] = refs;
        }

        var ordered = OrderRootsFirst(targets, graph);

        // path -> detached ProjectInfo (text materialised in-memory, ProjectReferences
        // stripped). Keep-first wins: a project pulled in transitively by an earlier
        // worker is captured once and not re-opened standalone.
        var captured = new ConcurrentDictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentDictionary<string, SkippedProject>(StringComparer.OrdinalIgnoreCase);

        var degree = GetLoadParallelism();
        var timeoutSec = GetOpenProjectTimeoutSec();
        using var gate = new SemaphoreSlim(degree);
        var loadedCount = 0;

        var tasks = new List<Task>(ordered.Count);
        foreach (var entry in ordered)
        {
            if (captured.ContainsKey(entry.Path))
                continue;

            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    // Re-check after acquiring the gate: an in-flight worker may have
                    // captured this project transitively while we were queued.
                    if (captured.ContainsKey(entry.Path))
                        return;

                    var index = Interlocked.Increment(ref loadedCount);
                    await Console.Error.WriteLineAsync(
                        $"[roslyn-codelens] Loading {index}/{targets.Count}: {entry.Name}").ConfigureAwait(false);

                    using var ws = MSBuildWorkspace.Create();
                    ws.WorkspaceFailed += (_, e) =>
                        Console.Error.WriteLine($"[roslyn-codelens] Warning: {e.Diagnostic.Message}");

                    Project? loaded;
                    try
                    {
                        loaded = await RunWithTimeoutAsync<Project>(
                            innerCt => ws.OpenProjectAsync(entry.Path, cancellationToken: innerCt),
                            timeoutSec,
                            ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await Console.Error.WriteLineAsync(
                            $"[roslyn-codelens] Skipping project {entry.Name}: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
                        failures[entry.Path] = new SkippedProject(entry.Path, entry.Name, "Failed",
                            $"{ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    if (loaded is null)
                    {
                        await Console.Error.WriteLineAsync(
                            $"[roslyn-codelens] Timeout loading project {entry.Name} (exceeded {timeoutSec}s).").ConfigureAwait(false);
                        failures[entry.Path] = new SkippedProject(entry.Path, entry.Name, "Timeout",
                            $"Project load exceeded {timeoutSec}s. " +
                            $"Set ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS to override.");
                        return;
                    }

                    // Capture this project plus every in-set project the workspace
                    // pulled in transitively, detaching each into a workspace-free
                    // ProjectInfo before the worker workspace is disposed.
                    foreach (var p in ws.CurrentSolution.Projects)
                    {
                        if (p.FilePath is null || !targetPaths.Contains(p.FilePath))
                            continue;
                        if (captured.ContainsKey(p.FilePath))
                            continue;
                        var info = await ToDetachedInfoAsync(p).ConfigureAwait(false);
                        captured.TryAdd(p.FilePath, info);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Re-stitch: rebuild every captured project into a single AdhocWorkspace,
        // re-wiring ProjectReferences from the on-disk graph (their original ids
        // were local to each worker workspace and are meaningless now).
        var idByPath = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, info) in captured)
            idByPath[path] = info.Id;

        var finalInfos = new List<ProjectInfo>(captured.Count);
        foreach (var (path, info) in captured)
        {
            var projectRefs = new List<ProjectReference>();
            foreach (var refPath in graph[path])
                if (idByPath.TryGetValue(refPath, out var refId))
                    projectRefs.Add(new ProjectReference(refId));
            finalInfos.Add(info.WithProjectReferences(projectRefs));
        }

        // Any target SDK project that is neither captured nor already recorded as a
        // failure could not be loaded — surface it through the same skip channel.
        foreach (var t in targets)
        {
            if (captured.ContainsKey(t.Path))
                continue;
            skipped.Add(failures.TryGetValue(t.Path, out var f)
                ? f
                : new SkippedProject(t.Path, t.Name, "Failed", "Project could not be loaded."));
        }

        var workspace = new AdhocWorkspace();
        var solutionInfo = SolutionInfo.Create(
            SolutionId.CreateNewId(), VersionStamp.Create(), solutionPath, finalInfos);
        workspace.AddSolution(solutionInfo);

        return (workspace.CurrentSolution, workspace, skipped);
    }

    /// <summary>
    /// Orders target projects by descending transitive in-set dependency count, so
    /// dependency roots open first. Their transitive loads populate the capture
    /// cache, letting leaf projects be skipped before they are ever scheduled.
    /// Ordering affects only efficiency, never correctness (keep-first dedup).
    /// </summary>
    private static List<ProjectClassifier.ClassifiedProject> OrderRootsFirst(
        IReadOnlyList<ProjectClassifier.ClassifiedProject> targets,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        int ClosureSize(string path)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!graph.TryGetValue(cur, out var refs))
                    continue;
                foreach (var r in refs)
                    if (seen.Add(r))
                        stack.Push(r);
            }
            return seen.Count;
        }

        return targets
            .OrderByDescending(t => ClosureSize(t.Path))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Detaches a workspace-loaded <see cref="Project"/> into a self-contained
    /// <see cref="ProjectInfo"/> with document text materialised in memory and all
    /// ProjectReferences stripped, so it survives disposal of its source workspace
    /// and can be re-stitched into a fresh workspace. The project keeps its own
    /// (globally-unique) id and document ids.
    /// </summary>
    private static async Task<ProjectInfo> ToDetachedInfoAsync(Project project)
    {
        var documents = new List<DocumentInfo>();
        foreach (var d in project.Documents)
            documents.Add(await ToDocumentInfoAsync(d, d.SourceCodeKind).ConfigureAwait(false));

        var additional = new List<DocumentInfo>();
        foreach (var d in project.AdditionalDocuments)
            additional.Add(await ToDocumentInfoAsync(d, SourceCodeKind.Regular).ConfigureAwait(false));

        var analyzerConfig = new List<DocumentInfo>();
        foreach (var d in project.AnalyzerConfigDocuments)
            analyzerConfig.Add(await ToDocumentInfoAsync(d, SourceCodeKind.Regular).ConfigureAwait(false));

        return ProjectInfo.Create(
                project.Id,
                VersionStamp.Create(),
                project.Name,
                project.AssemblyName,
                project.Language,
                filePath: project.FilePath,
                outputFilePath: project.OutputFilePath,
                compilationOptions: project.CompilationOptions,
                parseOptions: project.ParseOptions,
                documents: documents,
                projectReferences: Array.Empty<ProjectReference>(),
                metadataReferences: project.MetadataReferences,
                analyzerReferences: project.AnalyzerReferences,
                additionalDocuments: additional)
            .WithAnalyzerConfigDocuments(analyzerConfig)
            .WithDefaultNamespace(project.DefaultNamespace);
    }

    private static async Task<DocumentInfo> ToDocumentInfoAsync(TextDocument document, SourceCodeKind kind)
    {
        var text = await document.GetTextAsync().ConfigureAwait(false);
        var loader = TextLoader.From(
            TextAndVersion.Create(text, VersionStamp.Create(), document.FilePath ?? document.Name));

        return DocumentInfo.Create(
            document.Id,
            document.Name,
            folders: document.Folders,
            sourceCodeKind: kind,
            loader: loader,
            filePath: document.FilePath);
    }

    private static async Task<(Solution Solution, Workspace Workspace, IReadOnlyList<SkippedProject> Skipped)> OpenFilteredAsync(
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
