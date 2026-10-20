using DailyToolkit.Core.FilePack;

namespace DailyToolkit.Desktop.FilePack;

public sealed partial class FilePackViewModel
{
    private bool _idleManaged, _settingsVisible, _toolWindowsVisible, _presentationReleased, _draftWasSaved, _draftChanged;
    private PackTaskCounts _idleCounts;
    private CancellationTokenSource? _idleDelay;
    private Task _idleOperation = Task.CompletedTask;
    internal Task IdleWork => _idleOperation;
    internal bool IsIdleForMemoryCollection
    {
        get
        {
            if (_disposed || _shuttingDown || _enabled || HasActivePresentation || _loading || _collecting || _creating ||
                _loaded || _draftLoaded || _settingsDirty || _draftChanged || _preferencesGate.CurrentCount != 1 ||
                _enabledGate.CurrentCount != 1 || _operations.Count != 0 || _runtimeOperations.Count != 0 ||
                !SettingsReady.IsCompletedSuccessfully || !Manager.Ready.IsCompletedSuccessfully ||
                !_idleOperation.IsCompletedSuccessfully || (_initializing is not null && !_initializing.IsCompletedSuccessfully) ||
                Appearances.Any(appearance => !appearance.PendingSave.IsCompletedSuccessfully) || HasUnfinishedTasks ||
                Manager.IsInitialized && !Manager.IsSuspended ||
                Manager.GetTaskCounts() is not { Running: 0, Queued: 0, External: 0 }) return false;
            lock (_taskChangeSync) return !_taskChangeScheduled && _pendingTaskChanges.Count == 0;
        }
    }
    internal event Action? RuntimeReleased;
    private bool HasActivePresentation => _visible || _settingsVisible || _toolWindowsVisible;

    internal void SetSettingsVisible(bool visible)
    {
        _idleManaged = true; _settingsVisible = visible;
        if (visible) CancelIdleRelease(); else ScheduleIdleRelease();
    }
    internal void SetToolWindowsVisible(bool visible)
    {
        _idleManaged = true; _toolWindowsVisible = visible;
        if (visible) CancelIdleRelease(); else ScheduleIdleRelease();
    }
    private void CancelIdleRelease()
    {
        _idleDelay?.Cancel(); _idleDelay?.Dispose(); _idleDelay = null;
    }
    private void ScheduleIdleRelease()
    {
        if (!_idleManaged || HasActivePresentation || !_enabled || IsStopping ||
            !_loaded && !_draftLoaded && !Manager.IsInitialized) return;
        CancelIdleRelease();
        _idleDelay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _idleOperation = ReleaseWhenIdleAsync(_idleDelay.Token);
    }
    private async Task ReleaseWhenIdleAsync(CancellationToken token)
    {
        var entered = false;
        try
        {
            // Coalesce focus/window switches once; no recurring timer or polling.
            await Task.Delay(350, token);
            await _enabledGate.WaitAsync(token); entered = true;
            if (HasActivePresentation || !_enabled || IsStopping || _loading || _runtimeOperations.Count > 0) return;
            await SaveDraftCoreAsync();
            token.ThrowIfCancellationRequested();
            _idleCounts = Manager.IsInitialized ? Manager.GetTaskCounts() : _idleCounts;
            Entries.Clear(); Tasks.Clear(); _rows.Clear();
            lock (_taskChangeSync) _pendingTaskChanges.Clear();
            _loaded = false; _draftLoaded = false; _initializing = null; _presentationReleased = true;
            // Active compression owns its own immutable snapshot and continues.
            // Its terminal event schedules another one-shot attempt to unload.
            Manager.TryUnloadIdle();
            Notify(""); RefreshCommands();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (IsExpected(exception))
        { RecordDiagnostic(exception); Notice = $"待打包清单未能保存，已保留在内存中：{exception.Message}"; }
        finally { if (entered) _enabledGate.Release(); }
    }

    internal async Task<bool> SaveBeforeClosingWindowAsync()
    {
        try
        {
            if (_initializing is not null) await _initializing;
            await WaitForDraftAsync();
            await SaveDraftCoreAsync();
            return true;
        }
        catch (Exception exception) when (IsExpected(exception))
        { RecordDiagnostic(exception); Notice = $"清单未能保存，请稍后重试关闭：{exception.Message}"; return false; }
    }

    private async Task ReleaseRuntimeAsync()
    {
        if (_initializing is not null) await _initializing;
        await Manager.SuspendAsync();
        await SaveDraftCoreAsync();
        // Both the task records and the unsubmitted list are durable before their
        // UI rows are dropped. Re-enabling keeps recovery lazy until first use.
        RuntimeReleased?.Invoke();
        Entries.Clear(); Tasks.Clear(); _rows.Clear(); _openWhenPrepared.Clear();
        lock (_taskChangeSync) _pendingTaskChanges.Clear();
        _loaded = false; _draftLoaded = false; _initializing = null;
        _idleCounts = default;
        _presentationReleased = true;
        Notify(""); RefreshCommands();
    }

    private async Task SaveDraftCoreAsync()
    {
        if (!_draftLoaded || _disposed || Entries.Count == 0 && !_draftWasSaved && !_draftChanged) return;
        await Store.SaveDraftAsync(new(Entries.Select(row => new PackDraftEntry(row.Entry, row.PathDraft)).ToArray(),
            TaskName, ArchiveName, OutputDirectory, Mode, Format, Compression, NewFolderPath, MoveTargetPath));
        _draftWasSaved = true; _draftChanged = false;
    }

    private void RestoreDraft(PackDraft draft)
    {
        TaskName = draft.TaskName; ArchiveName = PackArchiveNaming.NormalizeDefault(draft.ArchiveName); OutputDirectory = draft.OutputDirectory;
        Mode = draft.Mode; Format = draft.Format; Compression = draft.Compression;
        NewFolderPath = draft.NewFolderPath; MoveTargetPath = draft.MoveTargetPath;
        ReplaceEntries(draft.Entries.Select(row => row.Entry));
        var paths = draft.Entries.ToDictionary(row => row.Entry.Id, row => row.PathDraft);
        foreach (var row in Entries) row.PathDraft = paths[row.Id];
    }
}
