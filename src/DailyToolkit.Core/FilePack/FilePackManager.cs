using System.Text.Json;

namespace DailyToolkit.Core.FilePack;

public sealed partial class FilePackManager : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly Dictionary<string, Guid> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _pendingTasks = [], _pendingSessions = [];
    private readonly HashSet<Task> _submissions = [];
    private readonly FilePackStore _store;
    private readonly SevenZipAdapter _engine;
    private readonly FilePackWorkspaceManager _workspace;
    private FilePackPreferences _preferences = new();
    private Task? _initialization, _shutdown, _suspension;
    private Task _queueSave = Task.CompletedTask;
    private long _queueOrder;
    private int _builtInRunning, _externalCopying;
    private bool _shuttingDown, _suspended;
    public event Action<PackTaskInfo>? TaskChanged;
    public Task Ready { get { lock (_sync) return _initialization ?? Task.CompletedTask; } }
    public FilePackPreferences Preferences { get { lock (_sync) return _preferences; } }
    public IReadOnlyList<PackTaskInfo> Tasks { get { lock (_sync) return _jobs.Values.Select(job => job.Info).OrderBy(task => task.QueueOrder).ToArray(); } }
    public bool IsSuspended { get { lock (_sync) return _suspended; } }
    public bool IsInitialized { get { lock (_sync) return _initialization?.IsCompletedSuccessfully == true; } }
    public bool TryGetTask(Guid taskId, out PackTaskInfo? task)
    { lock (_sync) { task = _jobs.TryGetValue(taskId, out var job) ? job.Info : null; return task is not null; } }
    public PackTaskCounts GetTaskCounts()
    {
        lock (_sync)
        {
            var running = 0; var queued = 0; var completed = 0; var failed = 0; var external = 0;
            foreach (var job in _jobs.Values)
                switch (job.Info.State)
                {
                    case PackState.Collecting: case PackState.Copying: case PackState.Compressing: case PackState.Verifying: running++; break;
                    case PackState.Queued: queued++; break;
                    case PackState.Completed: completed++; break;
                    case PackState.Failed: case PackState.Interrupted: failed++; break;
                    case PackState.AwaitingExternal: external++; break;
                }
            return new(running, queued, completed, failed, external);
        }
    }
    public int MaximumParallel
    {
        get { lock (_sync) return _preferences.MaximumParallel; }
        set { lock (_sync) _preferences = _preferences with { MaximumParallel = Math.Clamp(value, 1, 4) }; Pump(); }
    }

    public FilePackManager(FilePackStore store, SevenZipAdapter engine, FilePackWorkspaceManager? workspace = null)
    { _store = store; _engine = engine; _workspace = workspace ?? new(store.RootDirectory); }

    public Task InitializeAsync()
    {
        lock (_sync) { ThrowIfStopping(); return _initialization ??= Task.Run(InitializeCoreAsync); }
    }
    private async Task InitializeCoreAsync()
    {
        var preferences = await _store.LoadPreferencesAsync().ConfigureAwait(false);
        var tasks = await _store.LoadTasksAsync().ConfigureAwait(false);
        lock (_sync) _preferences = preferences;
        foreach (var loaded in tasks)
        {
            var task = loaded with { Snapshot = Freeze(loaded.Snapshot) };
            if (task.State is PackState.Queued or PackState.Collecting or PackState.Copying or PackState.Compressing or PackState.Verifying)
                task = task with { State = PackState.Interrupted, Error = "上次任务已中断，请检查来源后重新开始。", FinishedAt = DateTimeOffset.UtcNow };
            else if (task.State == PackState.AwaitingExternal)
            {
                if (task.Snapshot.Options.Mode != PackMode.External || !await _workspace.IsOwnedAsync(task.Snapshot).ConfigureAwait(false))
                    task = task with { State = PackState.Interrupted, Error = "外部工作区缺失或归属无法确认，原路径未被清理。", FinishedAt = DateTimeOffset.UtcNow };
                else task = task with { WorkspacePath = _workspace.GetWorkspacePath(task.Snapshot) };
            }
            // Freezing an entry list changes reference equality. It must not turn
            // an unchanged completed record into a needless write on every launch.
            if (task.State != loaded.State || task.Error != loaded.Error || task.WorkspacePath != loaded.WorkspacePath ||
                task.FinishedAt != loaded.FinishedAt)
                await _store.SaveTaskAsync(task).ConfigureAwait(false);
            lock (_sync) { _jobs.Add(task.TaskId, new(task)); _queueOrder = Math.Max(_queueOrder, task.QueueOrder); }
            Publish(task);
        }
    }

    public Task<PackTaskInfo> EnqueueAsync(PackSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Task<PackTaskInfo> operation;
        lock (_sync)
        {
            ThrowIfStopping();
            operation = EnqueueCoreAsync(snapshot, cancellationToken);
            _submissions.Add(operation);
        }
        _ = ForgetSubmissionAsync(operation);
        return operation;
    }
    private async Task ForgetSubmissionAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception) { /* The submitting caller observes the original failure. */ }
        finally { lock (_sync) _submissions.Remove(operation); }
    }
    private async Task<PackTaskInfo> EnqueueCoreAsync(PackSnapshot snapshot, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        var frozen = Freeze(snapshot);
        var plan = await Task.Run(() => new PackPlanner().Create(frozen), cancellationToken).ConfigureAwait(false);
        if (frozen.Options.Mode == PackMode.BuiltIn && plan.RequiresStaging && !frozen.Options.AllowStaging)
            throw new InvalidOperationException("当前目录结构需要临时复制，请确认额外空间后再创建任务。");
        PackTaskInfo task;
        lock (_sync)
        {
            ThrowIfStopping();
            if (_jobs.ContainsKey(frozen.TaskId) || _pendingTasks.Contains(frozen.TaskId) || _pendingSessions.Contains(frozen.SessionId) ||
                _jobs.Values.Any(job => job.Info.Snapshot.SessionId == frozen.SessionId))
                throw new InvalidOperationException("任务或工作区标识已被使用。");
            var output = frozen.Options.Mode == PackMode.BuiltIn ? ReserveOutput(frozen) : "";
            _pendingTasks.Add(frozen.TaskId); _pendingSessions.Add(frozen.SessionId);
            task = new(frozen, PackState.Queued, OutputPath: output, QueueOrder: ++_queueOrder);
        }
        try { await _store.SaveTaskAsync(task, cancellationToken).ConfigureAwait(false); }
        catch
        {
            lock (_sync) { ReleaseOutputLocked(task); _pendingTasks.Remove(frozen.TaskId); _pendingSessions.Remove(frozen.SessionId); }
            throw;
        }
        lock (_sync)
        {
            _pendingTasks.Remove(frozen.TaskId); _pendingSessions.Remove(frozen.SessionId);
            if (_shuttingDown) { ReleaseOutputLocked(task); throw new InvalidOperationException("程序正在退出，新任务未开始。"); }
            _jobs.Add(task.TaskId, new(task));
        }
        Publish(task); Pump(); return task;
    }

    public async Task SavePreferencesAsync(FilePackPreferences preferences)
    {
        await InitializeAsync().ConfigureAwait(false);
        var normalized = preferences.Normalize();
        await _store.SavePreferencesAsync(normalized).ConfigureAwait(false);
        lock (_sync) _preferences = normalized;
        Pump();
    }

    private void Pump()
    {
        lock (_sync)
        {
            if (_shuttingDown || _suspended) return;
            foreach (var job in _jobs.Values.Where(job => !job.Active && job.Info.State == PackState.Queued).OrderBy(job => job.Info.QueueOrder))
            {
                var external = job.Info.Snapshot.Options.Mode == PackMode.External;
                if (external ? _externalCopying >= 1 : _builtInRunning >= _preferences.MaximumParallel) continue;
                job.Active = true; job.Cancellation = new();
                if (external) _externalCopying++; else _builtInRunning++;
                job.Worker = Task.Run(() => RunAsync(job));
            }
        }
    }

    private async Task RunAsync(Job job)
    {
        var token = job.Cancellation!.Token; var snapshot = job.Info.Snapshot;
        try
        {
            await ChangeAsync(job, info => info with { State = PackState.Collecting, Error = "", FinishedAt = null }).ConfigureAwait(false);
            var plan = new PackPlanner().Create(snapshot);
            token.ThrowIfCancellationRequested();
            var progress = new TaskProgress(value => Report(job, value));
            var workspacePath = "";
            if (plan.RequiresStaging)
            {
                await ChangeAsync(job, info => info with { State = PackState.Copying, WorkspacePath = _workspace.GetWorkspacePath(snapshot) }).ConfigureAwait(false);
                workspacePath = await _workspace.PrepareAsync(snapshot, progress, token).ConfigureAwait(false);
            }
            if (snapshot.Options.Mode == PackMode.External)
            {
                token.ThrowIfCancellationRequested();
                await ChangeAsync(job, info => info with { State = PackState.AwaitingExternal, Percent = 100, WorkspacePath = workspacePath },
                    token, releaseExternalSlot: true).ConfigureAwait(false);
                Pump();
                return;
            }
            var temporaryOutput = await PrepareTemporaryOutputAsync(job.Info, token).ConfigureAwait(false);
            await ChangeAsync(job, info => info with { State = PackState.Compressing }).ConfigureAwait(false);
            await _engine.ExecuteAsync(snapshot, plan, workspacePath, temporaryOutput, MaximumParallel, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            CommitOutput(job.Info, temporaryOutput);
            var cleanupError = "";
            try
            {
                await CleanupTemporaryOutputAsync(snapshot).ConfigureAwait(false);
                if (workspacePath.Length > 0 && !snapshot.Options.KeepWorkspace)
                { await _workspace.CleanupAsync(snapshot).ConfigureAwait(false); workspacePath = ""; }
            }
            catch (Exception exception) when (IsExpectedError(exception)) { cleanupError = "压缩包已保存，临时文件未能清理：" + exception.Message; }
            await ChangeAsync(job, info => info with { State = PackState.Completed, Percent = 100,
                ProcessedBytes = snapshot.Entries.Sum(entry => entry.Size), ProcessedFiles = snapshot.Entries.Count(entry => !entry.IsDirectory),
                WorkspacePath = workspacePath, Error = cleanupError, FinishedAt = DateTimeOffset.UtcNow }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            bool interrupted; lock (_sync) interrupted = _shuttingDown;
            var cleanupError = interrupted ? "程序退出时中断，请重新开始。" : "";
            if (!interrupted)
                try { await CleanupTemporaryOutputAsync(snapshot).ConfigureAwait(false); await CleanupCopiedWorkspaceAsync(job).ConfigureAwait(false); }
                catch (Exception exception) when (IsExpectedError(exception)) { cleanupError = "任务已取消，临时文件未能清理：" + exception.Message; }
            await RecordFailureAsync(job, interrupted ? PackState.Interrupted : PackState.Cancelled, cleanupError).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            var error = exception.Message;
            try { await CleanupTemporaryOutputAsync(snapshot).ConfigureAwait(false); await CleanupCopiedWorkspaceAsync(job).ConfigureAwait(false); }
            catch (Exception cleanup) when (IsExpectedError(cleanup)) { error += " 临时文件未能清理：" + cleanup.Message; }
            await RecordFailureAsync(job, PackState.Failed, error).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (job.Active)
                {
                    job.Active = false;
                    if (snapshot.Options.Mode == PackMode.External) _externalCopying--; else _builtInRunning--;
                }
                if (job.Info.State != PackState.AwaitingExternal) ReleaseOutputLocked(job.Info);
                job.Cancellation?.Dispose(); job.Cancellation = null;
                job.Worker = Task.CompletedTask;
            }
            Pump();
        }
    }

    private async Task CleanupCopiedWorkspaceAsync(Job job)
    {
        if (job.Info.WorkspacePath.Length == 0 || job.Info.Snapshot.Options.KeepWorkspace) return;
        await _workspace.CleanupAsync(job.Info.Snapshot).ConfigureAwait(false);
        lock (_sync) job.Info = job.Info with { WorkspacePath = "" };
    }
    private async Task RecordFailureAsync(Job job, PackState state, string error)
    {
        try { await ChangeAsync(job, info => info with { State = state, Error = error, FinishedAt = DateTimeOffset.UtcNow }).ConfigureAwait(false); }
        catch (Exception exception) when (IsExpectedError(exception))
        {
            PackTaskInfo failed;
            lock (_sync)
            {
                job.Unsaved = true;
                failed = job.Info = job.Info with { State = state, Error = error + " 任务状态未能保存：" + exception.Message, FinishedAt = DateTimeOffset.UtcNow };
            }
            Publish(failed);
        }
    }
    private async Task ChangeAsync(Job job, Func<PackTaskInfo, PackTaskInfo> change,
        CancellationToken cancellationToken = default, bool releaseExternalSlot = false)
    {
        await job.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            PackTaskInfo next; lock (_sync) next = change(job.Info);
            await _store.SaveTaskAsync(next, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                job.Info = next;
                job.Unsaved = false;
                if (releaseExternalSlot && job.Active) { job.Active = false; _externalCopying--; }
            }
            Publish(next);
        }
        finally { job.Gate.Release(); }
    }
    private void Report(Job job, PackProgress progress)
    {
        PackTaskInfo next;
        lock (_sync)
        {
            if (!job.Active || job.Cancellation?.IsCancellationRequested != false) return;
            next = job.Info = job.Info with { State = progress.State, Percent = Math.Clamp(progress.Percent, 0, 100),
                ProcessedBytes = Math.Max(0, progress.ProcessedBytes), ProcessedFiles = Math.Max(0, progress.ProcessedFiles) };
        }
        Publish(next);
    }
    private void Publish(PackTaskInfo task)
    {
        var handlers = TaskChanged;
        if (handlers is null) return;
        foreach (Action<PackTaskInfo> handler in handlers.GetInvocationList())
            try { handler(task); } catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
    }
    private Job GetJob(Guid taskId) { lock (_sync) return _jobs.TryGetValue(taskId, out var job) ? job : throw new KeyNotFoundException("任务不存在。"); }
    private void ThrowIfStopping()
    {
        if (_shuttingDown) throw new InvalidOperationException("文件集装箱正在退出。");
        if (_suspended) throw new InvalidOperationException("文件集装箱已停用，请先开启。");
    }
    private static PackSnapshot Freeze(PackSnapshot snapshot) => snapshot with { Entries = Array.AsReadOnly(snapshot.Entries.Select(entry => entry with { }).ToArray()), Options = snapshot.Options with { } };
    private static bool IsExpectedError(Exception exception) => FilePackStore.IsStorageError(exception) || exception is ArgumentException
        or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException;
    private sealed class Job(PackTaskInfo info)
    {
        public PackTaskInfo Info = info;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public CancellationTokenSource? Cancellation;
        public Task Worker = Task.CompletedTask;
        public bool Active, Unsaved;
    }
    private sealed class TaskProgress(Action<PackProgress> report) : IProgress<PackProgress>
    { public void Report(PackProgress value) => report(value); }
}
