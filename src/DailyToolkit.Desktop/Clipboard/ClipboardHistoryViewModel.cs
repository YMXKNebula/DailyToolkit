using System.Collections.ObjectModel;
using System.Windows.Threading;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.ClipRecall;

public sealed partial class ClipboardHistoryViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SemaphoreSlim _settingsGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private ClipboardHistorySettings _settings = new();
    private Task? _opening;
    private Task _settingsTask = Task.CompletedTask;
    private Task _refreshTask = Task.CompletedTask;
    private Task _selectionTask = Task.CompletedTask, _actionTask = Task.CompletedTask;
    private Task? _disposeTask;
    private CancellationTokenSource? _searchCancellation, _selectionCancellation;
    private Timer? _cleanupTimer;
    private bool _loaded, _writable = true, _disposed, _favorite, _visible, _savedOnly, _busy;
    private bool _settingsDirty, _maintenanceQueued, _stopping, _loadingNote;
    private readonly Dictionary<Guid, string> _noteDrafts = [];
    private string _notice = "", _search = "", _editorText = "", _noteText = "";
    private int _filterIndex, _offset, _total;
    private ClipboardHistoryEntry? _selected;
    private ClipboardHistoryRecord? _detail;
    private ClipboardDeleteReceipt? _undo;
    public ClipboardSettingsStore Store { get; }
    public ClipboardHistoryRepository Repository { get; }
    public Task Ready { get; }
    internal Func<ClipboardHistoryRecord, Task<ClipboardOperationResult>>? RestoreRequested { get; set; }
    internal Action? PanelRequested { get; set; }
    internal event Action? SettingsCommitted;

    public ClipboardHistoryViewModel(ClipboardSettingsStore? store = null)
    {
        Store = store ?? new(); Repository = new(Store.DirectoryPath);
        InitializeAppearance();
        ToggleFavoriteCommand = new(_ => IsFavorite = !IsFavorite);
        Repository.Changed += RepositoryChanged; Ready = LoadAsync();
    }
    private async Task LoadAsync()
    {
        var result = await Store.LoadAsync();
        if (_disposed) return;
        _settings = result.Settings; _writable = result.Writable; Notice = result.Notice;
        CommitAppearance(_settings.PanelColors, resetDraft: true);
        _loaded = true; Notify("");
    }
    public ClipboardHistorySettings Settings => _settings;
    public bool IsLoaded => _loaded;
    public bool IsEnabled { get => _settings.IsEnabled; set => RequestEnabled(value); }
    public bool IsPaused { get => _settings.IsPaused; set => ChangeSettings(_settings with { IsPaused = value }); }
    public string Status => !IsEnabled ? "功能已停用" : IsPaused ? "监听已暂停" : "正在记录剪贴板";
    public string ShortcutText => _settings.ToggleShortcut?.Name ?? "未设置";
    public int MaximumRecords { get => _settings.MaximumRecords; set => ChangeSettings(_settings with { MaximumRecords = value }); }
    public int RetentionDays { get => _settings.RetentionDays; set => ChangeSettings(_settings with { RetentionDays = value }); }
    public int MaximumOrdinaryMegabytes { get => _settings.MaximumOrdinaryMegabytes; set => ChangeSettings(_settings with { MaximumOrdinaryMegabytes = value }); }
    public int MaximumTextKilobytes { get => _settings.MaximumTextKilobytes; set => ChangeSettings(_settings with { MaximumTextKilobytes = value }); }
    public bool IncludeRestoredInWindowsHistory { get => _settings.IncludeRestoredInWindowsHistory; set => ChangeSettings(_settings with { IncludeRestoredInWindowsHistory = value }); }
    public string ExcludedApplications
    {
        get => string.Join("\n", _settings.ExcludedApplications);
        set => ChangeSettings(_settings with { ExcludedApplications = value.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries) });
    }
    public bool IsFavorite { get => _favorite; set { if (Set(ref _favorite, value)) Notify(nameof(FavoriteHint)); } }
    public string FavoriteHint => IsFavorite ? "取消收藏剪贴回溯" : "收藏剪贴回溯";
    public RelayCommand ToggleFavoriteCommand { get; }
    public string Notice { get => _notice; set { if (Set(ref _notice, value)) Notify(nameof(HasNotice)); } }
    public bool HasNotice => Notice.Length > 0;
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
    public ObservableCollection<ClipboardHistoryEntry> Entries { get; } = [];
    public string Search { get => _search; set { if (Set(ref _search, value)) { _offset = 0; RequestRefresh(); } } }
    public bool SavedOnly { get => _savedOnly; set { if (Set(ref _savedOnly, value)) { _offset = 0; Notify(nameof(HistoryOnly)); RequestRefresh(); } } }
    public bool HistoryOnly { get => !SavedOnly; set { if (value) SavedOnly = false; } }
    public int FilterIndex { get => _filterIndex; set { if (Set(ref _filterIndex, value)) { _offset = 0; RequestRefresh(); } } }
    public string PageText => _total == 0 ? "暂无记录" : $"{_offset + 1}–{Math.Min(_offset + 100, _total)} / {_total}";
    public bool CanPrevious => _offset > 0;
    public bool CanNext => _offset + 100 < _total;
    public string StorageText { get; private set; } = "尚未创建历史数据";
    public bool IsVisible
    {
        get => _visible;
        internal set
        {
            if (!Set(ref _visible, value)) return;
            if (value) RequestRefresh();
            else { _searchCancellation?.Cancel(); _selectionCancellation?.Cancel(); Entries.Clear(); Selected = null; ReleaseMedia(); }
        }
    }
    public ClipboardHistoryEntry? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _detail = null; HasFileCache = false; SetLoadedNote(""); NotifyDetail();
            _selectionCancellation?.Cancel(); _selectionCancellation?.Dispose();
            _selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            if (value is not null) _selectionTask = LoadDetailAsync(value.Id, _selectionCancellation.Token);
        }
    }
    public string DetailText => _detail?.Content.DisplayText ?? "选择一条记录以查看完整内容。";
    public string DetailTime => _detail is null ? "" : $"创建于 {_detail.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" +
        (_detail.LastUsedAt is { } used ? $" · 最近恢复 {used.ToLocalTime():MM-dd HH:mm:ss}" : "");
    public bool HasSelection => _detail is not null;
    public bool CanEditManual => _detail?.IsManual == true && _detail.Content.Type is ClipboardContentType.Text or ClipboardContentType.Link;
    public string SaveText => _selected?.IsSaved == true ? "取消永久保存" : "永久保存";
    public string PinText => _selected?.IsPinned == true ? "取消置顶" : "置顶";
    public bool HasNoteChanges => _noteDrafts.Count > 0;
    public string NoteText
    {
        get => _noteText;
        set
        {
            if (!Set(ref _noteText, value) || _loadingNote || _detail is null) return;
            if (value == _detail.Note) _noteDrafts.Remove(_detail.Id); else _noteDrafts[_detail.Id] = value;
            Notify(nameof(HasNoteChanges));
        }
    }
    private void SetLoadedNote(string value)
    { _loadingNote = true; try { NoteText = value; } finally { _loadingNote = false; } }
    internal void DiscardNoteDrafts()
    { _noteDrafts.Clear(); SetLoadedNote(_detail?.Note ?? ""); Notify(nameof(HasNoteChanges)); }
    public string EditorText { get => _editorText; set => Set(ref _editorText, value); }
    public bool CanUndo => _undo is not null && DateTimeOffset.UtcNow < _undo.ExpiresAt;
    internal Task PendingSettings => _settingsTask;
    private void NotifyDetail()
    { Notify(nameof(DetailText)); Notify(nameof(DetailTime)); Notify(nameof(HasSelection)); Notify(nameof(CanEditManual)); Notify(nameof(SaveText)); Notify(nameof(PinText)); NotifyMedia(); }
    private async Task LoadDetailAsync(Guid id, CancellationToken token)
    {
        try
        {
            var detail = await Repository.GetSummaryAsync(id, token); token.ThrowIfCancellationRequested();
            var cached = detail?.Content.Type == ClipboardContentType.Files && await Repository.HasFileCacheAsync(id);
            token.ThrowIfCancellationRequested();
            if (_disposed || _selected?.Id != id) return;
            _detail = detail; HasFileCache = cached; SetLoadedNote(detail is not null && _noteDrafts.TryGetValue(id, out var draft) ? draft : detail?.Note ?? ""); NotifyDetail();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception)) { Notice = "完整内容未能读取，原数据保留。"; }
    }
    public void RequestEnabled(bool enabled)
    {
        if (!_loaded || enabled == IsEnabled) return;
        // The settings page gives the recording notice; the user's switch action enables capture.
        ChangeSettings(_settings with
        {
            IsEnabled = enabled,
            ConsentAccepted = _settings.ConsentAccepted || enabled,
            IsPaused = enabled ? false : _settings.IsPaused
        });
    }
    public bool SetShortcut(KeyboardShortcut? shortcut, IEnumerable<KeyboardShortcut?> reserved)
    {
        if (shortcut is { IsValid: false } || shortcut is not null && reserved.Any(shortcut.Matches))
        { Notice = "此快捷键无效或与现有工具冲突，请选择其他组合。"; return false; }
        return ChangeSettings(_settings with { ToggleShortcut = shortcut });
    }
    private bool ChangeSettings(ClipboardHistorySettings settings)
    {
        if (!_loaded || _disposed || _stopping) return false;
        if (!_writable) { Notice = "设置文件未能读取，已阻止覆盖。请检查原设置或显式恢复默认设置。"; return false; }
        _settings = settings.Normalize(); _settingsDirty = true; Notify("");
        _settingsTask = FlushSettingsAsync();
        return true;
    }
    public async Task<bool> FlushSettingsAsync()
    {
        await Ready; await _settingsGate.WaitAsync();
        try
        {
            if (!_settingsDirty) return true;
            if (!_writable) return false;
            var settings = _settings; await Store.SaveAsync(settings);
            CommitAppearance(settings.PanelColors);
            if (settings == _settings) _settingsDirty = false;
            SettingsCommitted?.Invoke(); QueueMaintenance(); return true;
        }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        { Notice = "剪贴回溯设置保存失败，请检查空间或权限；退出前请重试保存。"; return false; }
        finally { _settingsGate.Release(); }
    }
    public async Task ResetSettingsAsync()
    {
        _appearanceResetVersion++;
        await Ready; var requestedDraft = AppearanceDraft; _writable = true; _settings = new(); _settingsDirty = true; Notify("");
        if (await FlushSettingsAsync() && AppearanceDraft == requestedDraft) SetAppearanceDraft(Appearance);
    }
    internal async Task EnsureRepositoryAsync()
    {
        await Ready;
        _opening ??= Repository.InitializeAsync();
        try { await _opening; QueueMaintenance(); }
        catch { _opening = null; throw; }
    }
    internal void OpenPanel() => PanelRequested?.Invoke();
    internal void RepositoryChanged()
    {
        if (_disposed) return;
        _ = _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (IsVisible) RequestRefresh();
            QueueMaintenance();
        }));
    }
    internal void RequestRefresh()
    {
        if (_disposed || !IsVisible) return;
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refreshTask = RefreshAsync(_searchCancellation.Token);
    }
    private async Task RefreshAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(160, token); await EnsureRepositoryAsync(); token.ThrowIfCancellationRequested();
            var type = FilterIndex switch { 1 => ClipboardContentType.Text, 2 => ClipboardContentType.Link, 3 => ClipboardContentType.Image,
                4 => ClipboardContentType.Files, _ => (ClipboardContentType?)null };
            var page = await Repository.QueryAsync(new(Search, SavedOnly, type, _offset, 100), token);
            if (page.Total > 0 && _offset >= page.Total)
            { _offset = (page.Total - 1) / 100 * 100; page = await Repository.QueryAsync(new(Search, SavedOnly, type, _offset, 100), token); }
            var statistics = await Repository.StatisticsAsync(); token.ThrowIfCancellationRequested();
            var selectedId = Selected?.Id;
            // Diff a page by stable ID; one batch mutation causes one query, not one
            // complete UI rebuild per selected record.
            for (var index = 0; index < page.Records.Count; index++)
            {
                var entry = page.Records[index]; var previous = Entries.FirstOrDefault(value => value.Id == entry.Id);
                if (previous is null) Entries.Insert(index, entry);
                else
                {
                    var oldIndex = Entries.IndexOf(previous); if (oldIndex != index) Entries.Move(oldIndex, index);
                    if (previous != entry) Entries[index] = entry;
                }
            }
            while (Entries.Count > page.Records.Count) Entries.RemoveAt(Entries.Count - 1);
            _total = page.Total;
            SetStatistics(statistics);
            Selected = Entries.FirstOrDefault(entry => entry.Id == selectedId) ?? Entries.FirstOrDefault();
            if (Selected is { Type: ClipboardContentType.Files } fileEntry)
                HasFileCache = await Repository.HasFileCacheAsync(fileEntry.Id);
            Notify(nameof(StorageText)); Notify(nameof(PageText)); Notify(nameof(CanPrevious)); Notify(nameof(CanNext)); NotifyDetail();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        { Notice = "历史读取失败，原文件保留。请检查数据库或恢复最近备份。"; }
    }
    public void PreviousPage() { if (CanPrevious) { _offset = Math.Max(0, _offset - 100); RequestRefresh(); } }
    public void NextPage() { if (CanNext) { _offset += 100; RequestRefresh(); } }
    private void SetStatistics(ClipboardHistoryStatistics statistics)
    { StorageText = $"{statistics.Total} 条记录 · 永久保存 {statistics.Saved} 条 · 占用 {statistics.StorageBytes / 1048576d:0.##} MB"; Notify(nameof(StorageText)); }
    internal async Task RefreshStorageAsync()
    {
        await Ready;
        if (_disposed || !File.Exists(Repository.DatabasePath)) return;
        try { await EnsureRepositoryAsync(); SetStatistics(await Repository.StatisticsAsync()); }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception)) { Notice = "存储统计未能读取，原数据保留。"; }
    }
    private Task<bool> ActionAsync(Func<Task> action, bool initialize = true)
    {
        if (IsBusy || _disposed || _stopping) return Task.FromResult(false);
        var operation = ActionCoreAsync(action, initialize); _actionTask = operation; return operation;
    }
    private async Task<bool> ActionCoreAsync(Func<Task> action, bool initialize)
    {
        IsBusy = true; Notice = "";
        try
        {
            if (initialize) await EnsureRepositoryAsync();
            await action();
            try { SetStatistics(await Repository.StatisticsAsync()); }
            catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
            { Notice += " 操作已完成，存储统计暂未更新。"; }
            return true;
        }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        {
            Notice = exception is InvalidOperationException or InvalidDataException ? exception.Message :
                "操作未完整完成，请检查磁盘空间、权限或数据库占用，并刷新确认当前记录。"; return false;
        }
        finally { IsBusy = false; }
    }
    public Task<bool> SetFlagsAsync(IEnumerable<Guid> ids, bool? saved = null, bool? pinned = null) =>
        ActionAsync(() => Repository.SetFlagsAsync(ids, saved, pinned, Settings));
    public Task<bool> SaveNoteAsync()
    {
        if (_detail is null) return Task.FromResult(false);
        var id = _detail.Id; var note = NoteText;
        return ActionAsync(async () =>
        {
            await Repository.UpdateNoteAsync(id, note, Settings);
            if (_noteDrafts.TryGetValue(id, out var draft) && draft == note) _noteDrafts.Remove(id);
            if (_detail?.Id == id) _detail = _detail with { Note = note };
            Notify(nameof(HasNoteChanges));
        });
    }
    public Task<bool> CreateManualAsync(Guid? editing = null)
    {
        var text = EditorText;
        return ActionAsync(async () =>
        {
            var content = ClipboardContent.FromText(text);
            if (editing is { } id) await Repository.EditManualAsync(id, content, Settings);
            else await Repository.CaptureAsync(content, DateTimeOffset.UtcNow, null, Settings, manual: true);
            if (EditorText == text) EditorText = "";
        });
    }
    public Task<bool> DeleteAsync(IEnumerable<Guid> ids)
    {
        var selected = ids.Distinct().ToArray();
        return ActionAsync(async () =>
        {
            _undo = await Repository.DeleteAsync(selected); Notify(nameof(CanUndo));
            foreach (var id in selected) { _thumbnails.Remove(id); _thumbnailOrder.Remove(id); }
            foreach (var id in selected) _noteDrafts.Remove(id);
            if (_detail is not null && selected.Contains(_detail.Id)) SetLoadedNote(_detail.Note);
            Notify(nameof(HasNoteChanges));
            QueueMaintenance();
        });
    }
    public Task<bool> UndoAsync() => ActionAsync(async () =>
    {
        if (_undo is null) return;
        var count = await Repository.UndoAsync(_undo, Settings); _undo = null; Notify(nameof(CanUndo));
        if (count == 0) Notice = "撤销期限已过。";
    });
    public Task<bool> ClearAsync(bool includeSaved) => ActionAsync(async () =>
    {
        await Repository.ClearAsync(includeSaved); _undo = null; Notify(nameof(CanUndo));
        ReleaseMedia();
        foreach (var id in _noteDrafts.Keys.ToArray())
            if (includeSaved || await Repository.GetAsync(id) is null) _noteDrafts.Remove(id);
        Notify(nameof(HasNoteChanges));
    });
    public Task<bool> BackupAsync() => ActionAsync(async () => { await Repository.BackupAsync(); Notice = "备份已保存。"; });
    public Task<bool> RestoreBackupAsync() => ActionAsync(async () =>
    {
        await Repository.RestoreBackupAsync(); _opening = Task.CompletedTask; RequestRefresh();
        Notice = "已恢复备份。"; QueueMaintenance();
    }, initialize: false);
    public async Task<bool> RestoreSelectedAsync()
    {
        if (Selected is null) return false;
        var id = Selected.Id; var success = false;
        var handled = await ActionAsync(async () =>
        {
            var record = await Repository.GetAsync(id);
            if (record is null) { Notice = "记录已删除。"; return; }
            if (RestoreRequested is null) { Notice = "剪贴板恢复服务尚未就绪。"; return; }
            var prepared = await PrepareFileRestoreAsync(record);
            var fallback = UseCachedFiles && record.Content.Type == ClipboardContentType.Files && prepared == record;
            var result = await RestoreRequested(prepared); Notice = (fallback && result.Success ? "未缓存，使用原文件。 " : "") + result.Message; success = result.Success;
            if (result.Success && Selected?.Id == record.Id) { _detail = await Repository.GetSummaryAsync(record.Id); NotifyDetail(); }
        });
        return handled && success;
    }
    private void QueueMaintenance()
    {
        if (_disposed || !Repository.IsReady || _maintenanceQueued) return;
        _maintenanceQueued = true; _ = ScheduleMaintenanceAsync();
    }
    private async Task ScheduleMaintenanceAsync()
    {
        try
        {
            var next = await Repository.NextCleanupAsync(Settings);
            if (_disposed) return;
            _cleanupTimer?.Dispose(); _cleanupTimer = null;
            if (next is { } at)
            {
                var delay = at - DateTimeOffset.UtcNow;
                // Long deadlines are rescheduled daily, one wakeup and no polling.
                delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds + 20, 20, TimeSpan.FromDays(1).TotalMilliseconds));
                _cleanupTimer = new(_ => _dispatcher.BeginInvoke(new Action(() => _ = CleanupAsync())), null, delay, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception)) { Notice = "自动清理暂未完成，原历史保留。"; }
        finally { _maintenanceQueued = false; }
    }
    private async Task CleanupAsync()
    {
        if (_disposed) return;
        try { await Repository.CleanupAsync(Settings); Notify(nameof(CanUndo)); QueueMaintenance(); }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception)) { Notice = "自动清理失败，请检查数据目录。"; }
    }
    internal async Task<bool> PrepareExitAsync()
    {
        _stopping = true; var pendingAppearance = PendingAppearanceSave; var waitingForAppearance = !pendingAppearance.IsCompleted;
        await Task.WhenAll(_actionTask, pendingAppearance);
        if (waitingForAppearance && !await pendingAppearance) { _stopping = false; return false; }
        if (!await FlushSettingsAsync()) { _stopping = false; return false; }
        _lifetime.Cancel(); _cleanupTimer?.Dispose();
        try { await Task.WhenAll(_refreshTask, _selectionTask, _imageTask); } catch (OperationCanceledException) { }
        return true;
    }
    public void Dispose() => _ = DisposeAsync();
    public ValueTask DisposeAsync()
    { _disposeTask ??= DisposeCoreAsync(); return new(_disposeTask); }
    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _cleanupTimer?.Dispose(); Repository.Changed -= RepositoryChanged;
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose(); _selectionCancellation?.Cancel(); _selectionCancellation?.Dispose();
        await Task.WhenAll(_actionTask, _settingsTask, PendingAppearanceSave, _refreshTask, _selectionTask, _imageTask, Ready);
        ReleaseMedia(); DiscardNoteDrafts(); await Repository.DisposeAsync();
    }
}
