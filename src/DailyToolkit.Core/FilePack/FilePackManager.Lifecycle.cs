namespace DailyToolkit.Core.FilePack;

public sealed partial class FilePackManager
{
    public async Task CancelAsync(Guid taskId)
    {
        await InitializeAsync().ConfigureAwait(false);
        var job = GetJob(taskId); Task? worker = null; CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            if (job.Active) { worker = job.Worker; cancellation = job.Cancellation; }
            else if (job.Info.State is not (PackState.Queued or PackState.AwaitingExternal)) return;
            else { job.Info = job.Info with { State = PackState.Cancelled }; ReleaseOutputLocked(job.Info); }
        }
        if (worker is not null)
        {
            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
            await worker.ConfigureAwait(false); return;
        }
        // An external waiting workspace may contain the user's finished archive.
        // It stays available until an explicit completion or cleanup action.
        await ChangeAsync(job, info => info with { State = PackState.Cancelled, FinishedAt = DateTimeOffset.UtcNow }).ConfigureAwait(false);
        Pump();
    }

    public async Task<PackTaskInfo> RetryAsync(Guid taskId)
    {
        await InitializeAsync().ConfigureAwait(false);
        var job = GetJob(taskId);
        lock (_sync)
        {
            ThrowIfStopping();
            if (job.Active || job.Info.State is not (PackState.Failed or PackState.Cancelled or PackState.Interrupted))
                throw new InvalidOperationException("只有已停止的失败、取消或中断任务可以重新开始。");
        }
        await CleanupAsync(taskId).ConfigureAwait(false);
        var snapshot = Freeze(job.Info.Snapshot with { SessionId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow });
        await Task.Run(() => new PackPlanner().Create(snapshot)).ConfigureAwait(false);
        PackTaskInfo queued;
        lock (_sync)
        {
            ThrowIfStopping();
            var output = snapshot.Options.Mode == PackMode.BuiltIn ? ReserveOutput(snapshot) : "";
            queued = new(snapshot, PackState.Queued, OutputPath: output, QueueOrder: ++_queueOrder);
        }
        try { await ChangeAsync(job, _ => queued).ConfigureAwait(false); }
        catch { ReleaseOutput(queued); throw; }
        Pump(); return queued;
    }

    public void MoveQueued(Guid taskId, int direction)
    {
        Job[] changed;
        lock (_sync)
        {
            ThrowIfStopping();
            var queued = _jobs.Values.Where(job => !job.Active && job.Info.State == PackState.Queued).OrderBy(job => job.Info.QueueOrder).ToArray();
            var current = Array.FindIndex(queued, job => job.Info.TaskId == taskId);
            if (current < 0 || direction == 0) return;
            var target = Math.Clamp(current + Math.Sign(direction), 0, queued.Length - 1);
            if (target == current) return;
            var first = queued[current]; var second = queued[target];
            (first.Info, second.Info) = (first.Info with { QueueOrder = second.Info.QueueOrder }, second.Info with { QueueOrder = first.Info.QueueOrder });
            changed = [first, second];
            var previousSave = _queueSave;
            _queueSave = Task.Run(() => SaveQueueOrderAsync(previousSave, changed));
        }
        foreach (var job in changed) Publish(job.Info);
    }
    private async Task SaveQueueOrderAsync(Task previous, Job[] jobs)
    {
        await previous.ConfigureAwait(false);
        foreach (var job in jobs)
            try { await ChangeAsync(job, info => info).ConfigureAwait(false); }
            catch (Exception exception) when (IsExpectedError(exception))
            {
                PackTaskInfo updated; lock (_sync) updated = job.Info = job.Info with { Error = "队列顺序未能保存：" + exception.Message };
                Publish(updated);
            }
    }

    public async Task CompleteExternalAsync(Guid taskId, bool keepWorkspace = false)
    {
        await InitializeAsync().ConfigureAwait(false);
        var job = GetJob(taskId);
        lock (_sync)
            if (job.Active || job.Info.Snapshot.Options.Mode != PackMode.External || job.Info.State != PackState.AwaitingExternal)
                throw new InvalidOperationException("外部任务尚未准备完成。");
        if (!await _workspace.IsOwnedAsync(job.Info.Snapshot).ConfigureAwait(false)) throw new IOException("工作区归属无法确认，已阻止清理。");
        if (!keepWorkspace) await _workspace.CleanupAsync(job.Info.Snapshot).ConfigureAwait(false);
        await ChangeAsync(job, info => info with { State = PackState.Completed, Percent = 100, Error = "",
            WorkspacePath = keepWorkspace ? _workspace.GetWorkspacePath(info.Snapshot) : "", FinishedAt = DateTimeOffset.UtcNow }).ConfigureAwait(false);
    }

    public async Task CleanupAsync(Guid taskId)
    {
        await InitializeAsync().ConfigureAwait(false);
        var job = GetJob(taskId);
        lock (_sync) if (job.Active || job.Info.State == PackState.Queued) throw new InvalidOperationException("请先停止任务再清理。");
        await CleanupTemporaryOutputAsync(job.Info.Snapshot).ConfigureAwait(false);
        if (job.Info.WorkspacePath.Length > 0)
            await _workspace.CleanupAsync(job.Info.Snapshot).ConfigureAwait(false);
        await ChangeAsync(job, info => info with { WorkspacePath = "" }).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid taskId)
    {
        await InitializeAsync().ConfigureAwait(false);
        var job = GetJob(taskId);
        lock (_sync)
            if (job.Active || job.Info.State is PackState.Queued or PackState.AwaitingExternal || job.Info.WorkspacePath.Length > 0)
                throw new InvalidOperationException("请先完成或清理任务的临时目录，再移除记录。");
        await CleanupTemporaryOutputAsync(job.Info.Snapshot).ConfigureAwait(false);
        await _store.RemoveTaskAsync(taskId).ConfigureAwait(false);
        lock (_sync) { _jobs.Remove(taskId); ReleaseOutputLocked(job.Info); }
    }

    public Task ShutdownAsync()
    {
        lock (_sync)
        {
            if (_shutdown is { IsFaulted: true } or { IsCanceled: true }) _shutdown = null;
            return _shutdown ??= Task.Run(ShutdownCoreAsync);
        }
    }
    private async Task ShutdownCoreAsync()
    {
        Task? initialization; lock (_sync) { _shuttingDown = true; initialization = _initialization; }
        if (initialization is null) return;
        await initialization.ConfigureAwait(false);
        Job[] active; Job[] queued; Task queueSave;
        lock (_sync)
        {
            active = _jobs.Values.Where(job => job.Active).ToArray();
            queued = _jobs.Values.Where(job => !job.Active && job.Info.State == PackState.Queued).ToArray();
            foreach (var job in queued) job.Info = job.Info with { State = PackState.Interrupted,
                Error = "退出时未执行，请重新开始。", FinishedAt = DateTimeOffset.UtcNow };
            queueSave = _queueSave;
        }
        foreach (var job in active) try { job.Cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        await Task.WhenAll(active.Select(job => job.Worker)).ConfigureAwait(false);
        try { await queueSave.ConfigureAwait(false); }
        catch (Exception exception) when (IsExpectedError(exception))
        { lock (_sync) if (ReferenceEquals(_queueSave, queueSave)) _queueSave = Task.CompletedTask; }
        // Retry a failed exit save without leaving queued jobs or owned processes running.
        Job[] interrupted; lock (_sync) interrupted = _jobs.Values.Where(job => job.Info.State == PackState.Interrupted).ToArray();
        foreach (var job in interrupted)
        {
            await ChangeAsync(job, info => info).ConfigureAwait(false);
            ReleaseOutput(job.Info);
        }
    }
    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
