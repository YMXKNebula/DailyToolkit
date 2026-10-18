using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;
using Microsoft.Win32;

namespace DailyToolkit.Desktop.FilePack;

public sealed partial class FilePackViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly FileCollectionService _collection = new();
    private readonly PackPlanner _planner = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<string> _openDirectory;
    private readonly HashSet<Guid> _openWhenPrepared = [];
    private readonly Dictionary<Guid, FilePackTaskViewModel> _rows = [];
    private readonly object _taskChangeSync = new();
    private readonly Dictionary<Guid, PackTaskInfo> _pendingTaskChanges = [];
    private bool _taskChangeScheduled;
    private readonly HashSet<Task> _operations = [];
    private readonly HashSet<Task> _runtimeOperations = [];
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private readonly SemaphoreSlim _enabledGate = new(1, 1);
    private CancellationTokenSource _activity = new();
    private Task? _initializing, _shutdownTask;
    private CancellationTokenSource? _collectionCancellation;
    private bool _loaded, _loading, _favorite, _visible, _collecting, _creating, _shuttingDown, _disposed, _draftLoaded;
    private string _notice = "", _taskName = "文件集装箱", _archiveName = "文件集装箱", _outputDirectory = "";
    private string _newFolderPath = "新建文件夹", _moveTargetPath = "";
    private PackMode _mode = PackMode.BuiltIn;
    private PackFormat _format = PackFormat.Zip;
    private PackCompression _compression = PackCompression.Standard;
    private int _maximumParallel = 2;
    private long _draftBytes;
    private bool _batchEntries;
    private bool _askOutputEachTime, _keepExternalWorkspace;

    public FilePackViewModel(FilePackStore? store = null, string? enginePath = null, Action<string>? openDirectory = null, SevenZipAdapter? engine = null)
    {
        Store = store ?? new(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "DailyToolkit", "FilePack"));
        Manager = new(Store, engine ?? new SevenZipAdapter(enginePath ?? Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7za.exe")));
        _openDirectory = openDirectory ?? OpenDirectoryWithExplorer;
        Manager.TaskChanged += TaskChanged;
        ToggleFavoriteCommand = new(_ => IsFavorite = !IsFavorite);
        AddFilesCommand = new(_ => PickFiles(), () => CanEditDraft);
        AddFoldersCommand = new(_ => PickFolders(), () => CanEditDraft);
        RemoveSelectedCommand = new(parameter => RemoveEntries(SelectedRows(parameter)), () => CanEditDraft && Entries.Count > 0);
        ClearCommand = new(_ => ClearDraft(), () => CanEditDraft && Entries.Count > 0);
        AddVirtualFolderCommand = new(_ => AddVirtualFolder(NewFolderPath), () => CanEditDraft);
        MoveSelectedCommand = new(parameter => MoveEntries(SelectedRows(parameter), MoveTargetPath), () => CanEditDraft && Entries.Count > 0);
        CheckDuplicatesCommand = new(_ => CheckDuplicates(), () => CanEditDraft && Entries.Count > 0);
        BrowseOutputCommand = new(_ => PickOutputDirectory(), () => CanEditDraft);
        CreateTaskCommand = new(_ => Track(CreateTaskAsync()), () => CanEditDraft && Entries.Count > 0);
        CancelCollectionCommand = new(_ => _collectionCancellation?.Cancel(), () => IsCollecting);
        CancelTaskCommand = new(parameter => Act(parameter, row => Manager.CancelAsync(row.TaskId)));
        RetryTaskCommand = new(parameter => Act(parameter, RetryTaskAsync), () => IsEnabled && !_shuttingDown && !_disposed);
        MoveUpCommand = new(parameter => MoveQueued(parameter, -1));
        MoveDownCommand = new(parameter => MoveQueued(parameter, 1));
        OpenTaskCommand = new(parameter => { if (parameter is FilePackTaskViewModel row) OpenTask(row); });
        CompleteExternalCommand = new(parameter => Act(parameter, row => FinishExternalAsync(row, row.Info.Snapshot.Options.KeepWorkspace)));
        KeepExternalCommand = new(parameter => Act(parameter, row => FinishExternalAsync(row, !row.Info.Snapshot.Options.KeepWorkspace)));
        CleanupTaskCommand = new(parameter => Act(parameter, CleanupTaskAsync));
        RemoveTaskCommand = new(parameter => Act(parameter, row => Manager.RemoveAsync(row.TaskId)));
        Entries.CollectionChanged += (_, change) =>
        {
            if (_draftLoaded) _draftChanged = true;
            if (_batchEntries) return;
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) _draftBytes = 0;
            else
            {
                if (change.OldItems is not null) foreach (FilePackEntryViewModel row in change.OldItems) _draftBytes -= row.Entry.Size;
                if (change.NewItems is not null) foreach (FilePackEntryViewModel row in change.NewItems) _draftBytes += row.Entry.Size;
            }
            Notify(nameof(DraftSummary)); RefreshCommands();
        };
        InitializeSettings();
    }

    public FilePackStore Store { get; }
    public FilePackManager Manager { get; }
    public Task Ready => Task.WhenAll(SettingsReady, _initializing ?? Task.CompletedTask);
    internal Task PendingOperations => Task.WhenAll(_operations.ToArray());
    public ObservableCollection<FilePackEntryViewModel> Entries { get; } = [];
    public ObservableCollection<FilePackTaskViewModel> Tasks { get; } = [];
    public IReadOnlyList<FilePackChoice<PackMode>> Modes { get; } = [new("内置压缩", PackMode.BuiltIn), new("外部软件", PackMode.External)];
    public IReadOnlyList<FilePackChoice<PackFormat>> Formats { get; } = [new("ZIP", PackFormat.Zip), new("7z", PackFormat.SevenZip), new("TAR", PackFormat.Tar)];
    public IReadOnlyList<FilePackChoice<PackCompression>> CompressionLevels { get; } = [new("不压缩", PackCompression.Store),
        new("快速", PackCompression.Fast), new("标准", PackCompression.Standard), new("最大", PackCompression.Maximum)];
    public IReadOnlyList<int> ParallelChoices { get; } = [1, 2, 3, 4];
    public Func<string, string, bool>? ConfirmAction { get; set; }
    public Func<string, FilePackOutputChoice>? ResolveOutputConflict { get; set; }
    public Func<string, FilePackEntryConflictChoice>? ResolveEntryConflict { get; set; }
    public Func<string?>? SelectOutputDirectory { get; set; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFoldersCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand AddVirtualFolderCommand { get; }
    public RelayCommand MoveSelectedCommand { get; }
    public RelayCommand CheckDuplicatesCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand CreateTaskCommand { get; }
    public RelayCommand CancelCollectionCommand { get; }
    public RelayCommand CancelTaskCommand { get; }
    public RelayCommand RetryTaskCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand OpenTaskCommand { get; }
    public RelayCommand CompleteExternalCommand { get; }
    public RelayCommand KeepExternalCommand { get; }
    public RelayCommand CleanupTaskCommand { get; }
    public RelayCommand RemoveTaskCommand { get; }

    public bool IsLoaded => _loaded;
    internal bool IsStopping => _shuttingDown || _disposed;
    public bool IsInitializing => _loading;
    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (!Set(ref _visible, value)) return;
            if (value) { CancelIdleRelease(); Track(InitializeAsync()); }
            else ScheduleIdleRelease();
        }
    }
    public bool IsFavorite { get => _favorite; set { if (Set(ref _favorite, value)) { Notify(nameof(FavoriteSymbol)); Notify(nameof(FavoriteHint)); } } }
    public string FavoriteSymbol => IsFavorite ? "★" : "☆";
    public string FavoriteHint => IsFavorite ? "取消收藏文件集装箱" : "收藏文件集装箱";
    public string Notice { get => _notice; private set { if (Set(ref _notice, value)) Notify(nameof(HasNotice)); } }
    public bool HasNotice => Notice.Length > 0;
    public bool IsCollecting { get => _collecting; private set { if (Set(ref _collecting, value)) DraftStateChanged(); } }
    public bool IsCreating { get => _creating; private set { if (Set(ref _creating, value)) DraftStateChanged(); } }
    public bool CanEditDraft => IsEnabled && _loaded && !_collecting && !_creating && !_shuttingDown && !_disposed;
    public bool HasUnfinishedTasks => IsInitializing || IsCollecting || IsCreating || RunningCount > 0 || WaitingCount > 0 || ExternalCount > 0;
    public string Status => IsInitializing ? "正在读取任务" : RunningCount > 0 ? $"正在执行 {RunningCount} 个任务" : !IsEnabled ? "已停用" : !_loaded ? "就绪" : IsCollecting ? "正在收集文件" :
        WaitingCount > 0 ? $"等待执行 {WaitingCount} 个任务" : ExternalCount > 0 ? $"等待外部操作 {ExternalCount} 个任务" : "就绪";
    public int RunningCount => _presentationReleased ? _idleCounts.Running : Tasks.Count(row => row.IsRunning);
    public int WaitingCount => _presentationReleased ? _idleCounts.Queued : Tasks.Count(row => row.IsQueued);
    public int CompletedCount => _presentationReleased ? _idleCounts.Completed : Tasks.Count(row => row.Info.State == PackState.Completed);
    public int FailedCount => _presentationReleased ? _idleCounts.Failed : Tasks.Count(row => row.Info.State is PackState.Failed or PackState.Interrupted);
    public int ExternalCount => _presentationReleased ? _idleCounts.External : Tasks.Count(row => row.IsExternalReady);
    public string TaskSummary => $"执行 {RunningCount} · 排队 {WaitingCount} · 完成 {CompletedCount} · 失败 {FailedCount}" +
        (ExternalCount == 0 ? "" : $" · 外部 {ExternalCount}");
    public string DraftSummary => $"{Entries.Count} 项 · {FormatBytes(_draftBytes)}";
    public string TaskName { get => _taskName; set { if (Set(ref _taskName, value) && _draftLoaded) _draftChanged = true; } }
    public string ArchiveName { get => _archiveName; set { if (Set(ref _archiveName, value)) { if (_draftLoaded) _draftChanged = true; Notify(nameof(OutputPreview)); } } }
    public string OutputDirectory { get => _outputDirectory; set { if (Set(ref _outputDirectory, value)) { if (_draftLoaded) _draftChanged = true; Notify(nameof(OutputPreview)); } } }
    public string NewFolderPath { get => _newFolderPath; set { if (Set(ref _newFolderPath, value) && _draftLoaded) _draftChanged = true; } }
    public string MoveTargetPath { get => _moveTargetPath; set { if (Set(ref _moveTargetPath, value) && _draftLoaded) _draftChanged = true; } }
    public PackMode Mode { get => _mode; set { if (Set(ref _mode, value)) { if (_draftLoaded) _draftChanged = true; Notify(nameof(IsBuiltIn)); Notify(nameof(IsExternal)); Notify(nameof(CanChooseCompression)); Notify(nameof(CreateButtonText)); } } }
    public bool IsBuiltIn => Mode == PackMode.BuiltIn;
    public bool IsExternal => !IsBuiltIn;
    public string CreateButtonText => IsBuiltIn ? "创建压缩任务" : "准备外部工作区";
    public PackFormat Format { get => _format; set { if (Set(ref _format, value)) { if (_draftLoaded) _draftChanged = true; Notify(nameof(CanChooseCompression)); Notify(nameof(OutputPreview)); } } }
    public PackCompression Compression { get => _compression; set { if (Set(ref _compression, value) && _draftLoaded) _draftChanged = true; } }
    public bool CanChooseCompression => IsBuiltIn && Format != PackFormat.Tar;
    public string OutputPreview
    {
        get { try { return string.IsNullOrWhiteSpace(OutputDirectory) ? "选择保存文件夹" : FilePackPaths.GetOutputPath(DraftOptions()); }
            catch (Exception exception) when (IsExpected(exception)) { return "请填写有效的文件名与保存位置"; } }
    }
    public int MaximumParallel { get => _maximumParallel; set { value = Math.Clamp(value, 1, 4); if (Set(ref _maximumParallel, value) && _settingsLoaded) SavePreferences(); } }
    public bool AskOutputEachTime { get => _askOutputEachTime; set { if (Set(ref _askOutputEachTime, value) && _settingsLoaded) SavePreferences(); } }
    public bool KeepExternalWorkspace { get => _keepExternalWorkspace; set { if (Set(ref _keepExternalWorkspace, value) && _settingsLoaded) SavePreferences(); } }

    public Task InitializeAsync()
    {
        if (_disposed || _shuttingDown || !_enabled) return Task.CompletedTask;
        return _initializing ??= InitializeCoreAsync();
    }
    private async Task InitializeCoreAsync()
    {
        _loading = true; Notify(nameof(IsInitializing)); NotifyTaskState();
        try
        {
            await SettingsReady;
            if (!_enabled || _disposed || _shuttingDown) return;
            await Manager.InitializeAsync();
            if (_disposed || _shuttingDown || !_enabled) return;
            if (!_draftLoaded)
            {
                var draft = await Store.LoadDraftAsync();
                if (draft is not null) RestoreDraft(draft);
                _draftWasSaved = draft is not null; _draftChanged = false;
                _draftLoaded = true;
            }
            _presentationReleased = false;
            foreach (var task in Manager.Tasks) Upsert(task);
            _loaded = true;
            SortTasks();
            Notice = string.Join(System.Environment.NewLine, Store.Diagnostics);
            Notify(""); RefreshCommands();
        }
        catch (Exception exception) when (IsExpected(exception)) { RecordDiagnostic(exception); Notice = $"任务记录未能打开：{exception.Message}"; }
        finally { _loading = false; Notify(nameof(IsInitializing)); NotifyTaskState(); }
    }

    public Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (!CanEditDraft) return Task.CompletedTask;
        var operation = AddPathsCoreAsync(paths.ToArray()); TrackRuntime(operation); return operation;
    }
    private async Task AddPathsCoreAsync(string[] paths)
    {
        IsCollecting = true;
        _collectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _activity.Token);
        try
        {
            Notice = "";
            var existing = Entries.Select(row => row.Entry).ToArray();
            var known = existing.Select(entry => entry.Id).ToHashSet();
            var result = await _collection.CollectAsync(paths, existing, _collectionCancellation.Token);
            _collectionCancellation.Token.ThrowIfCancellationRequested();
            var renamed = result.Entries.Where(entry => !known.Contains(entry.Id) && !string.IsNullOrEmpty(entry.SourcePath) &&
                !string.Equals(entry.ArchivePath, Path.GetRelativePath(entry.SourceRoot, entry.SourcePath).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToArray();
            var entries = result.Entries;
            if (renamed.Length > 0)
            {
                var details = string.Join(System.Environment.NewLine, renamed.Take(12).Select(entry => $"{Path.GetFileName(entry.SourcePath)} → {entry.ArchivePath}")) +
                    (renamed.Length > 12 ? $"{System.Environment.NewLine}另有 {renamed.Length - 12} 项" : "");
                var choice = (ResolveEntryConflict ?? FilePackDialogs.EntryConflict)(details);
                if (choice == FilePackEntryConflictChoice.Cancel) { Notice = ""; return; }
                if (choice == FilePackEntryConflictChoice.Skip)
                { var skipped = renamed.Select(entry => entry.Id).ToHashSet(); entries = result.Entries.Where(entry => !skipped.Contains(entry.Id)).ToArray(); }
            }
            ReplaceEntries(entries);
            Notice = string.Join(System.Environment.NewLine, result.Warnings.Where(warning => !warning.StartsWith("包内重名", StringComparison.Ordinal)).Take(12));
        }
        catch (OperationCanceledException) { Notice = ""; }
        catch (Exception exception) when (IsExpected(exception)) { Notice = $"文件未能添加：{exception.Message}"; }
        finally { _collectionCancellation.Dispose(); _collectionCancellation = null; IsCollecting = false; }
    }

    public bool TryRenameEntry(FilePackEntryViewModel row, string archivePath)
    {
        if (!CanEditDraft || !Entries.Contains(row)) { if (!Entries.Contains(row)) row.ResetDraft(); return false; }
        try
        {
            archivePath = FilePackPaths.NormalizeArchivePath(archivePath);
            var old = row.Entry.ArchivePath;
            var mapped = Entries.Select(item => item.Entry.Id == row.Id ? item.Entry with { ArchivePath = archivePath } :
                row.Entry.IsDirectory && item.Entry.ArchivePath.StartsWith(old + "/", StringComparison.OrdinalIgnoreCase) ?
                    item.Entry with { ArchivePath = archivePath + item.Entry.ArchivePath[old.Length..] } : item.Entry).ToArray();
            FilePackPaths.ValidateEntries(mapped); ReplaceEntries(mapped); Notice = ""; return true;
        }
        catch (Exception exception) when (IsExpected(exception)) { row.ResetDraft(); Notice = $"包内路径未能修改：{exception.Message}"; return false; }
    }
    public bool AddVirtualFolder(string archivePath)
    {
        if (!CanEditDraft) return false;
        try
        {
            var entry = new PackEntry(Guid.NewGuid(), "", FilePackPaths.NormalizeArchivePath(archivePath), true, 0, null, "");
            FilePackPaths.ValidateEntries(Entries.Select(row => row.Entry).Append(entry));
            Entries.Add(new(entry)); Notice = ""; return true;
        }
        catch (Exception exception) when (IsExpected(exception)) { Notice = $"文件夹未能创建：{exception.Message}"; return false; }
    }
    public bool MoveEntries(IEnumerable<FilePackEntryViewModel> selected, string target)
    {
        if (!CanEditDraft) return false;
        try
        {
            var folder = string.IsNullOrWhiteSpace(target) ? "" : FilePackPaths.NormalizeArchivePath(target) + "/";
            var present = Entries.ToHashSet();
            var moves = selected.Where(present.Contains).Select(row => row.Entry).OrderBy(entry => entry.ArchivePath.Length).ToArray();
            var mapped = Entries.Select(row => row.Entry).ToArray();
            var roots = new Dictionary<string, PackEntry>(StringComparer.OrdinalIgnoreCase);
            PackEntry? FindRoot(string path)
            {
                if (roots.TryGetValue(path, out var exact)) return exact;
                while (path.LastIndexOf('/') is var split && split >= 0)
                { path = path[..split]; if (roots.TryGetValue(path, out var ancestor) && ancestor.IsDirectory) return ancestor; }
                return null;
            }
            foreach (var move in moves)
                if (FindRoot(move.ArchivePath) is null) roots.Add(move.ArchivePath, move);
            for (var index = 0; index < mapped.Length; index++)
                if (FindRoot(mapped[index].ArchivePath) is { } root)
                    mapped[index] = mapped[index] with { ArchivePath = folder + Path.GetFileName(root.ArchivePath) + mapped[index].ArchivePath[root.ArchivePath.Length..] };
            FilePackPaths.ValidateEntries(mapped); ReplaceEntries(mapped); Notice = ""; return true;
        }
        catch (Exception exception) when (IsExpected(exception)) { Notice = $"条目未能移动：{exception.Message}"; return false; }
    }
    public void RemoveEntries(IEnumerable<FilePackEntryViewModel> selected)
    {
        if (!CanEditDraft) return;
        var ids = selected.Select(row => row.Id).ToHashSet();
        ReplaceEntries(Entries.Where(row => !ids.Contains(row.Id)).Select(row => row.Entry).ToArray());
        Notice = "";
    }
    public void ClearDraft() { if (!CanEditDraft) return; Entries.Clear(); Notice = ""; }
    public void CheckDuplicates()
    {
        if (!CanEditDraft) return;
        try
        {
            var duplicate = Entries.Where(row => row.Entry.SourcePath.Length > 0).GroupBy(row =>
                FilePackPaths.NormalizeSourcePath(row.Entry.SourcePath), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToArray();
            FilePackPaths.ValidateEntries(Entries.Select(row => row.Entry));
            Notice = duplicate.Length == 0 ? "未发现重复或路径冲突。" : $"发现 {duplicate.Length} 组重复来源。";
        }
        catch (Exception exception) when (IsExpected(exception)) { Notice = exception.Message; }
    }

    public Task<PackTaskInfo?> CreateTaskAsync()
        => CreateTaskAsync(quick: false);
    public Task<PackTaskInfo?> CreateQuickTaskAsync()
        => CreateTaskAsync(quick: true);
    private Task<PackTaskInfo?> CreateTaskAsync(bool quick)
    {
        if (!CanEditDraft || Entries.Count == 0) return Task.FromResult<PackTaskInfo?>(null);
        var task = CreateTaskCoreAsync(quick); TrackRuntime(task); return task;
    }
    private async Task<PackTaskInfo?> CreateTaskCoreAsync(bool quick)
    {
        IsCreating = true;
        var taskId = Guid.NewGuid();
        try
        {
            CommitPathDrafts();
            var entries = Entries.Select(row => row.Entry with { }).ToArray();
            FilePackPaths.ValidateEntries(entries);
            var quickSettings = _quickPack;
            var options = quick ? new PackOptions(PackMode.BuiltIn, quickSettings.Format,
                quickSettings.Format == PackFormat.Tar ? PackCompression.Store : quickSettings.Compression,
                quickSettings.OutputDirectory.Trim(), quickSettings.ArchiveName.Trim(), PackOutputPolicy.AutoRename) : DraftOptions();
            var name = quick ? options.ArchiveName : string.IsNullOrWhiteSpace(TaskName) ? ArchiveName.Trim() : TaskName.Trim();
            if (options.Mode == PackMode.BuiltIn)
            {
                if (quick && string.IsNullOrWhiteSpace(options.OutputDirectory))
                {
                    var chosen = SelectOutputDirectory is not null ? SelectOutputDirectory() : ChooseDirectory();
                    if (string.IsNullOrEmpty(chosen)) return null;
                    QuickOutputDirectory = chosen;
                    options = options with { OutputDirectory = chosen };
                }
                else if (!quick && (AskOutputEachTime || string.IsNullOrWhiteSpace(OutputDirectory)))
                { if (!PickOutputDirectory()) { Notice = ""; return null; } options = DraftOptions(); }
                while (!quick)
                {
                    var path = FilePackPaths.GetOutputPath(options);
                    var reserved = Manager.Tasks.Any(task => task.Snapshot.Options.Mode == PackMode.BuiltIn &&
                        task.State is PackState.Queued or PackState.Collecting or PackState.Copying or PackState.Compressing or PackState.Verifying &&
                        string.Equals(task.OutputPath, path, StringComparison.OrdinalIgnoreCase));
                    if (!File.Exists(path) && !Directory.Exists(path) && !reserved) break;
                    var choice = (ResolveOutputConflict ?? FilePackDialogs.OutputConflict)(path + (reserved ? "\n已有未完成任务使用此路径。" : ""));
                    if (choice == FilePackOutputChoice.Cancel) { Notice = ""; return null; }
                    if (choice == FilePackOutputChoice.Reselect)
                    { if (!PickOutputDirectory()) { Notice = ""; return null; } options = DraftOptions(); continue; }
                    if (choice == FilePackOutputChoice.Overwrite)
                    {
                        if (reserved) throw new IOException("此名称有未完成任务，请选择自动更名或重新选择保存位置。");
                        if (Directory.Exists(path)) throw new IOException("目标名称已被文件夹占用，请重新选择或自动更名。");
                        options = options with { OutputPolicy = PackOutputPolicy.Overwrite, ApprovedOverwriteStamp = FilePackPaths.CaptureSourceStamp(path) };
                    }
                    else options = options with { OutputPolicy = PackOutputPolicy.AutoRename };
                    break;
                }
            }
            var snapshot = new PackSnapshot(taskId, Guid.NewGuid(), name,
                entries, options, DateTimeOffset.UtcNow);
            var planningSnapshot = snapshot;
            var plan = await Task.Run(() => _planner.Create(planningSnapshot), _activity.Token);
            _activity.Token.ThrowIfCancellationRequested();
            if (plan.RequiresStaging)
            {
                var message = options.Mode == PackMode.External ? $"将复制文件到独立工作区，需要约 {FormatBytes(plan.RequiredStagingBytes)} 额外空间。" :
                    $"此包内布局需要临时副本，额外占用约 {FormatBytes(plan.RequiredStagingBytes)}。";
                if (!(ConfirmAction ?? FilePackDialogs.Confirm)("准备临时文件", message)) { Notice = ""; return null; }
                snapshot = snapshot with { Options = options with { AllowStaging = true } };
            }
            if (snapshot.Options.Mode == PackMode.External) _openWhenPrepared.Add(taskId);
            var info = await Manager.EnqueueAsync(snapshot, _activity.Token);
            Upsert(info);
            if (info.State == PackState.AwaitingExternal) OpenPreparedExternal(info);
            var preferencesSaved = await SavePreferencesCoreAsync();
            Entries.Clear();
            try { await SaveDraftCoreAsync(); if (preferencesSaved) Notice = ""; }
            catch (Exception exception) when (IsExpected(exception)) { Notice = $"任务已创建，待打包清单未能保存：{exception.Message}"; }
            return info;
        }
        catch (OperationCanceledException) { _openWhenPrepared.Remove(taskId); Notice = ""; return null; }
        catch (Exception exception) when (IsExpected(exception)) { RecordDiagnostic(exception); _openWhenPrepared.Remove(taskId); Notice = $"任务未能创建：{exception.Message}"; return null; }
        finally { IsCreating = false; }
    }
    private void CommitPathDrafts()
    {
        foreach (var row in Entries.ToArray())
        {
            if (row.PathDraft == row.ArchivePath) continue;
            var path = FilePackPaths.NormalizeArchivePath(row.PathDraft);
            var previous = row.Entry.ArchivePath;
            var mapped = Entries.Select(item => item.Id == row.Id ? item.Entry with { ArchivePath = path } :
                row.Entry.IsDirectory && item.ArchivePath.StartsWith(previous + "/", StringComparison.OrdinalIgnoreCase) ?
                    item.Entry with { ArchivePath = path + item.ArchivePath[previous.Length..] } : item.Entry).ToArray();
            FilePackPaths.ValidateEntries(mapped); ReplaceEntries(mapped);
        }
    }
    private PackOptions DraftOptions() => new(Mode, Format, Format == PackFormat.Tar ? PackCompression.Store : Compression,
        OutputDirectory.Trim(), ArchiveName.Trim(), KeepWorkspace: Mode == PackMode.External && KeepExternalWorkspace);

    private void PickFiles()
    {
        var dialog = new OpenFileDialog { Title = "添加文件", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog() == true) _ = AddPathsAsync(dialog.FileNames);
    }
    private void PickFolders()
    {
        var dialog = new OpenFolderDialog { Title = "添加文件夹", Multiselect = true };
        if (dialog.ShowDialog() == true) _ = AddPathsAsync(dialog.FolderNames);
    }
    public bool PickOutputDirectory()
    {
        var path = SelectOutputDirectory is not null ? SelectOutputDirectory() : ChooseDirectory();
        if (string.IsNullOrEmpty(path)) return false;
        OutputDirectory = path; return true;
    }
    private string? ChooseDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "选择保存文件夹", Multiselect = false };
        if (Directory.Exists(OutputDirectory)) dialog.InitialDirectory = OutputDirectory;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    private static IEnumerable<FilePackEntryViewModel> SelectedRows(object? parameter) =>
        parameter is IEnumerable selected ? selected.OfType<FilePackEntryViewModel>().ToArray() : [];

    private async Task RetryTaskAsync(FilePackTaskViewModel row)
    {
        if (!IsEnabled || !row.CanRetry) return;
        var external = row.Info.Snapshot.Options.Mode == PackMode.External;
        if (external && row.Info.WorkspacePath.Length > 0 && !(ConfirmAction ?? FilePackDialogs.Confirm)("重新准备工作区",
            "上次工作区将被删除。请先移出要保留的文件。")) return;
        // Retry may return a replacement task ID; subscribe before queueing so fast preparation is observed.
        if (external) _openWhenPrepared.Add(row.TaskId);
        try
        {
            var info = await Manager.RetryAsync(row.TaskId);
            if (external && info.TaskId != row.TaskId) { _openWhenPrepared.Remove(row.TaskId); _openWhenPrepared.Add(info.TaskId); }
            Upsert(info); if (info.State == PackState.AwaitingExternal) OpenPreparedExternal(info);
        }
        catch { _openWhenPrepared.Remove(row.TaskId); throw; }
    }
    private async Task FinishExternalAsync(FilePackTaskViewModel row, bool keep)
    {
        if (!row.IsExternalReady) return;
        if (!keep && !(ConfirmAction ?? FilePackDialogs.Confirm)("完成并清理",
            "将删除工作区内的全部文件。请先移出压缩包和要保留的文件。")) return;
        await Manager.CompleteExternalAsync(row.TaskId, keep); Notice = "";
    }
    private async Task CleanupTaskAsync(FilePackTaskViewModel row)
    {
        if (!row.CanCleanup) return;
        if (!(ConfirmAction ?? FilePackDialogs.Confirm)("清理临时文件", "将删除工作区内的全部文件。请先移出要保留的文件。")) return;
        if (row.IsExternalReady) await Manager.CompleteExternalAsync(row.TaskId);
        else await Manager.CleanupAsync(row.TaskId);
        Notice = "";
    }
    private void MoveQueued(object? parameter, int direction)
    {
        if (parameter is not FilePackTaskViewModel { IsQueued: true } row || !IsEnabled || _shuttingDown) return;
        try { Manager.MoveQueued(row.TaskId, direction); }
        catch (Exception exception) when (IsExpected(exception)) { Notice = $"队列顺序未能修改：{exception.Message}"; }
    }
    private void OpenTask(FilePackTaskViewModel row)
    {
        try
        {
            var location = row.Location;
            var directory = row.Info.Snapshot.Options.Mode == PackMode.External ? location : Path.GetDirectoryName(location);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) { Notice = "文件夹尚未生成或已不存在。"; return; }
            _openDirectory(directory);
        }
        catch (Exception exception) when (IsExpected(exception)) { Notice = $"文件夹未能打开：{exception.Message}"; }
    }
    private static void OpenDirectoryWithExplorer(string directory)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(directory); Process.Start(start)?.Dispose();
    }
    private void OpenPreparedExternal(PackTaskInfo info)
    {
        if (info.State != PackState.AwaitingExternal || !_openWhenPrepared.Remove(info.TaskId) || !IsEnabled || _disposed || _shuttingDown) return;
        try { _openDirectory(info.WorkspacePath); }
        catch (Exception exception) when (IsExpected(exception)) { Notice = $"工作区已准备好，文件夹未能自动打开：{exception.Message}"; }
    }
    private void TaskChanged(PackTaskInfo info)
    {
        if (_disposed || !IsEnabled) return;
        if (!_dispatcher.CheckAccess())
        {
            lock (_taskChangeSync)
            {
                _pendingTaskChanges[info.TaskId] = info;
                if (_taskChangeScheduled) return;
                _taskChangeScheduled = true;
            }
            // At most one pending dispatcher callback; progress replaces its task's
            // previous snapshot while a busy/hidden UI catches up.
            _dispatcher.BeginInvoke(DrainTaskChanges, DispatcherPriority.Background);
            return;
        }
        ApplyTaskChanged(info);
    }
    private void DrainTaskChanges()
    {
        PackTaskInfo[] changes;
        lock (_taskChangeSync)
        {
            changes = _pendingTaskChanges.Values.ToArray(); _pendingTaskChanges.Clear(); _taskChangeScheduled = false;
        }
        if (_disposed || !IsEnabled) return;
        foreach (var info in changes) ApplyTaskChanged(info);
    }
    private void ApplyTaskChanged(PackTaskInfo info)
    {
        if (!_presentationReleased) Upsert(info);
        else if (Manager.IsInitialized)
        {
            var counts = Manager.GetTaskCounts();
            if (counts != _idleCounts) { _idleCounts = counts; NotifyTaskState(); }
        }
        if (info.State is PackState.Failed or PackState.Cancelled or PackState.Interrupted) _openWhenPrepared.Remove(info.TaskId);
        if (Manager.TryGetTask(info.TaskId, out var current) && current is not null) OpenPreparedExternal(current);
        if (info.State is PackState.AwaitingExternal or PackState.Completed or PackState.Cancelled or PackState.Failed or PackState.Interrupted)
            ScheduleIdleRelease();
    }
    private void Upsert(PackTaskInfo info)
    {
        // A worker can advance while its earlier notification waits for the UI dispatcher.
        Manager.TryGetTask(info.TaskId, out var current);
        if (current is null)
        {
            if (_rows.Remove(info.TaskId, out var removed)) Tasks.Remove(removed);
            NotifyTaskState(); return;
        }
        info = current;
        var reorder = true;
        if (_rows.TryGetValue(info.TaskId, out var row))
        {
            if (ReferenceEquals(row.Info, info)) return;
            reorder = row.Info.State != info.State || row.Info.QueueOrder != info.QueueOrder || row.Info.Snapshot.CreatedAt != info.Snapshot.CreatedAt;
            row.Update(info);
        }
        else { row = new(info); _rows.Add(info.TaskId, row); Tasks.Add(row); }
        if (reorder && !_loading) SortTasks();
        if (reorder) NotifyTaskState();
    }
    private void SortTasks()
    {
        var sorted = Tasks.OrderBy(row => row.IsRunning ? 0 : row.IsQueued ? 1 : row.IsExternalReady ? 2 : 3)
            .ThenBy(row => row.IsQueued ? row.Info.QueueOrder : 0).ThenByDescending(row => row.Info.Snapshot.CreatedAt).ToArray();
        for (var index = 0; index < sorted.Length; index++)
        { if (ReferenceEquals(Tasks[index], sorted[index])) continue; var current = Tasks.IndexOf(sorted[index]); Tasks.Move(current, index); }
    }
    private void ReplaceEntries(IEnumerable<PackEntry> entries)
    {
        var byId = Entries.ToDictionary(row => row.Id);
        var next = entries.ToArray();
        var nextIds = next.Select(entry => entry.Id).ToHashSet();
        _batchEntries = true;
        try
        {
            if (next.Length == 0) Entries.Clear();
            else
            {
                for (var index = Entries.Count - 1; index >= 0; index--)
                    if (!nextIds.Contains(Entries[index].Id)) Entries.RemoveAt(index);
                foreach (var entry in next)
                { if (byId.TryGetValue(entry.Id, out var row)) { if (row.Entry != entry) row.Update(entry); } else Entries.Add(new(entry)); }
            }
        }
        finally
        {
            _batchEntries = false; _draftBytes = Entries.Sum(row => row.Entry.Size);
            Notify(nameof(DraftSummary)); RefreshCommands();
        }
    }
    private void Act(object? parameter, Func<FilePackTaskViewModel, Task> operation)
    { if (parameter is FilePackTaskViewModel row && IsEnabled && !_shuttingDown && !_disposed) TrackRuntime(HandleActionAsync(row, operation)); }
    private async Task HandleActionAsync(FilePackTaskViewModel row, Func<FilePackTaskViewModel, Task> operation)
    {
        try
        {
            await operation(row);
            var tasks = Manager.Tasks;
            foreach (var removed in Tasks.Where(item => !tasks.Any(info => info.TaskId == item.TaskId)).ToArray())
            { Tasks.Remove(removed); _rows.Remove(removed.TaskId); }
            NotifyTaskState();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (IsExpected(exception)) { RecordDiagnostic(exception); Notice = $"操作未能完成：{exception.Message}"; }
    }
    private void SavePreferences() { _settingsDirty = true; Track(SavePreferencesCoreAsync()); }
    private async Task<bool> SavePreferencesCoreAsync(bool throwOnFailure = false)
    {
        await _preferencesGate.WaitAsync();
        try
        {
            if (!_settingsLoaded || _disposed) return true;
            var preferences = CurrentPreferences();
            if (Manager.IsInitialized && !Manager.IsSuspended) await Manager.SavePreferencesAsync(preferences);
            else await Store.SavePreferencesAsync(preferences);
            _settingsDirty = false;
            return true;
        }
        catch (Exception exception) when (IsExpected(exception))
        { RecordDiagnostic(exception); Notice = $"设置未能保存：{exception.Message}"; if (throwOnFailure) throw; return false; }
        finally { _preferencesGate.Release(); }
    }
    private void Track(Task operation)
    {
        if (operation.IsCompleted) return;
        if (!_operations.Add(operation)) return;
        _ = ObserveAsync(operation);
    }
    private void TrackRuntime(Task operation)
    {
        if (!operation.IsCompleted) _runtimeOperations.Add(operation);
        Track(operation);
    }
    internal async Task WaitForDraftAsync()
    { while ((_collecting || _creating) && _runtimeOperations.Count > 0) await Task.WhenAll(_runtimeOperations.ToArray()); }
    private async Task ObserveAsync(Task operation)
    {
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) Notice = $"操作未能完成：{exception.Message}"; }
        finally { _operations.Remove(operation); _runtimeOperations.Remove(operation); ScheduleIdleRelease(); }
    }
    private void DraftStateChanged() { Notify(nameof(CanEditDraft)); NotifyTaskState(); RefreshCommands(); }
    private void NotifyTaskState()
    { Notify(nameof(HasUnfinishedTasks)); Notify(nameof(Status)); Notify(nameof(RunningCount)); Notify(nameof(WaitingCount)); Notify(nameof(CompletedCount));
        Notify(nameof(FailedCount)); Notify(nameof(ExternalCount)); Notify(nameof(TaskSummary)); }
    private void RefreshCommands()
    { AddFilesCommand.Refresh(); AddFoldersCommand.Refresh(); RemoveSelectedCommand.Refresh(); ClearCommand.Refresh(); AddVirtualFolderCommand.Refresh();
        MoveSelectedCommand.Refresh(); CheckDuplicatesCommand.Refresh(); BrowseOutputCommand.Refresh(); CreateTaskCommand.Refresh(); CancelCollectionCommand.Refresh();
        QuickPackCommand?.Refresh(); OpenQuickWindowCommand?.Refresh(); OpenPackWindowCommand?.Refresh(); RetryTaskCommand.Refresh(); }

    public async Task FlushAsync()
    {
        await SettingsReady;
        await Task.WhenAll(Appearances.Select(appearance => appearance.PendingSave));
        if (_initializing is not null) await _initializing;
        await SaveDraftCoreAsync();
        if ((_loaded || _settingsDirty) && !_disposed) await SavePreferencesCoreAsync(throwOnFailure: true);
    }
    public Task ShutdownAsync()
    {
        if (_shutdownTask is { IsFaulted: true } or { IsCanceled: true }) _shutdownTask = null;
        return _shutdownTask ??= ShutdownCoreAsync();
    }
    private async Task ShutdownCoreAsync()
    {
        _shuttingDown = true; CancelIdleRelease(); _lifetime.Cancel(); _activity.Cancel(); _collectionCancellation?.Cancel(); DraftStateChanged();
        await _idleOperation;
        while (_operations.Count > 0)
        { try { await Task.WhenAll(_operations.ToArray()); } catch (Exception exception) when (exception is OperationCanceledException || IsExpected(exception)) { }
            _operations.RemoveWhere(task => task.IsCompleted); }
        await FlushAsync();
        if (_initializing is not null) await _initializing;
        await Manager.ShutdownAsync();
        _openWhenPrepared.Clear(); NotifyTaskState();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _shuttingDown = true; CancelIdleRelease(); _lifetime.Cancel(); _activity.Cancel(); _collectionCancellation?.Cancel();
        Manager.TaskChanged -= TaskChanged; _openWhenPrepared.Clear();
        _ = DisposeManagerAsync();
    }
    private async Task DisposeManagerAsync()
    { try { await Manager.DisposeAsync(); } catch (Exception) { /* Normal shutdown reports failures before disposal. */ } }
    public async ValueTask DisposeAsync() { await ShutdownAsync(); Dispose(); }
    private static void RecordDiagnostic(Exception exception) => DiagnosticReportService.Shared.RecordError(DiagnosticModule.FilePack, exception);
    internal static bool IsExpected(Exception exception) => exception is IOException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or NotSupportedException;
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double size = bytes; string[] units = ["KB", "MB", "GB", "TB"];
        foreach (var unit in units) { size /= 1024; if (size < 1024 || unit == units[^1]) return $"{size:0.#} {unit}"; }
        return "";
    }
}
