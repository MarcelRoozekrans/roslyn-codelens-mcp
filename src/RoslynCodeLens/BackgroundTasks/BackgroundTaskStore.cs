using System.Collections.Concurrent;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.BackgroundTasks;

public sealed class BackgroundTaskStore : IDisposable
{
    private static readonly TimeSpan EvictionAge = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ListWindow = TimeSpan.FromMinutes(5);
    private static readonly string[] s_adjectives =
        ["bold", "calm", "deft", "eager", "fast", "grim", "hale", "kind", "loud", "neat",
         "proud", "quick", "rare", "swift", "true", "vast", "warm", "wise", "young", "zen"];
    private static readonly string[] s_nouns =
        ["arch", "bird", "comet", "dawn", "echo", "flame", "grove", "harbor", "iris", "jolt",
         "kite", "lake", "moss", "nova", "oak", "pier", "quill", "reef", "stone", "tide"];

    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new(StringComparer.Ordinal);
    private readonly Timer _evictionTimer;

    public BackgroundTaskStore()
    {
        _evictionTimer = new Timer(_ => Evict(), state: null,
            dueTime: TimeSpan.FromMinutes(1), period: TimeSpan.FromMinutes(1));
    }

    public BackgroundTaskInfo Start(string toolName, Func<CancellationToken, Task<object?>> work)
    {
        var taskId = GenerateTaskId(toolName);
        var task = new BackgroundTask(taskId, toolName);
        _tasks[taskId] = task;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(task.Cts.Token).ConfigureAwait(false);
                task.Result = result;
                task.Status = BackgroundTaskStatus.Succeeded;
            }
            catch (OperationCanceledException)
            {
                task.Status = BackgroundTaskStatus.Cancelled;
            }
            catch (McpToolException mcpEx)
            {
                // Populate payload fields *before* the terminal Status flip so a
                // polling consumer that observes Status != Running cannot also
                // observe null ErrorCode/ErrorMessage.
                task.ErrorCode = mcpEx.Code.ToString();
                task.ErrorMessage = mcpEx.Message;
                task.Status = BackgroundTaskStatus.Failed;
            }
            catch (Exception ex)
            {
                task.ErrorCode = nameof(ToolErrorCode.Internal);
                task.ErrorMessage = ex.Message;
                task.Status = BackgroundTaskStatus.Failed;
            }
            finally
            {
                task.CompletedAt = DateTimeOffset.UtcNow;
            }
        });

        return task.ToInfo();
    }

    public BackgroundTaskInfo? Get(string taskId) =>
        _tasks.TryGetValue(taskId, out var task) ? task.ToInfo() : null;

    public IReadOnlyList<BackgroundTaskInfo> ListRunning()
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<BackgroundTaskInfo>();
        foreach (var task in _tasks.Values)
        {
            if (task.Status == BackgroundTaskStatus.Running)
                list.Add(task.ToInfo());
            else if (task.CompletedAt is { } completed && now - completed < ListWindow)
                list.Add(task.ToInfo());
        }
        return list;
    }

    private void Evict()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _tasks)
        {
            var task = kvp.Value;
            if (task.Status == BackgroundTaskStatus.Running) continue;
            if (task.CompletedAt is { } completed && now - completed >= EvictionAge)
            {
                if (_tasks.TryRemove(kvp.Key, out var removed))
                    removed.Dispose();
            }
        }
    }

    private static string GenerateTaskId(string toolName)
    {
        var adj = s_adjectives[Random.Shared.Next(s_adjectives.Length)];
        var noun = s_nouns[Random.Shared.Next(s_nouns.Length)];
        return $"bg-{toolName}-{adj}-{noun}";
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
        foreach (var task in _tasks.Values) task.Dispose();
        _tasks.Clear();
    }
}
