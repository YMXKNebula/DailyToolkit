using DailyToolkit.Core.FilePack;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.FilePack;

public sealed partial class FilePackViewModel
{
    private bool _enabled = true, _settingsLoaded, _settingsDirty;
    private KeyboardShortcut? _toggleShortcut = new(3, 0x50, "Ctrl + Alt + P");
    private QuickPackPreferences _quickPack = new();
    private FilePackShortcutWindow _shortcutWindow;
    private FilePackWindowPlacement? _detailedWindowPlacement;
    // Keep older settings-page colors in storage; the page now uses application resources.
    private Dictionary<string, string> _settingsColors = [], _packColors = [], _quickColors = [];
    private FilePackAppearance _selectedAppearance = null!;
    private string _shortcutNotice = "";
    public Task SettingsReady { get; private set; } = Task.CompletedTask;
    public bool SettingsLoaded => _settingsLoaded;
    public bool IsEnabled => _enabled;
    public KeyboardShortcut? ToggleShortcut => _toggleShortcut;
    public string ShortcutText => ToggleShortcut?.Name ?? "未设置";
    public IReadOnlyList<FilePackChoice<FilePackShortcutWindow>> ShortcutWindows { get; } =
        [new("快速装载", FilePackShortcutWindow.Quick), new("详细打包", FilePackShortcutWindow.Detailed)];
    public FilePackShortcutWindow ShortcutWindow
    {
        get => _shortcutWindow;
        set { if (Enum.IsDefined(value) && Set(ref _shortcutWindow, value) && _settingsLoaded) SavePreferences(); }
    }
    internal FilePackWindowPlacement? DetailedWindowPlacement => _detailedWindowPlacement;
    public Action? QuickWindowRequested { get; set; }
    public Action? PackWindowRequested { get; set; }
    public RelayCommand ToggleEnabledCommand { get; private set; } = null!;
    public RelayCommand OpenQuickWindowCommand { get; private set; } = null!;
    public RelayCommand OpenPackWindowCommand { get; private set; } = null!;
    public RelayCommand QuickPackCommand { get; private set; } = null!;
    public RelayCommand BrowseQuickOutputCommand { get; private set; } = null!;
    public FilePackAppearance PackAppearance { get; private set; } = null!;
    public FilePackAppearance QuickAppearance { get; private set; } = null!;
    public IReadOnlyList<FilePackAppearance> Appearances { get; private set; } = [];
    public FilePackAppearance SelectedAppearance { get => _selectedAppearance; set { if (value is not null) Set(ref _selectedAppearance, value); } }
    public PackFormat QuickFormat { get => _quickPack.Format; set { if (!Enum.IsDefined(value) || value == QuickFormat) return;
        _quickPack = _quickPack with { Format = value }; Notify(); Notify(nameof(QuickCanChooseCompression)); SavePreferences(); } }
    public PackCompression QuickCompression { get => _quickPack.Compression; set { if (!Enum.IsDefined(value) || value == QuickCompression) return;
        _quickPack = _quickPack with { Compression = value }; Notify(); SavePreferences(); } }
    public bool QuickCanChooseCompression => QuickFormat != PackFormat.Tar;
    public string QuickOutputDirectory { get => _quickPack.OutputDirectory; set { if (value == QuickOutputDirectory) return;
        _quickPack = _quickPack with { OutputDirectory = value }; Notify(); SavePreferences(); } }
    public string QuickArchiveName { get => _quickPack.ArchiveName; set { if (value == QuickArchiveName) return;
        _quickPack = _quickPack with { ArchiveName = value }; Notify(); SavePreferences(); } }
    private void InitializeSettings()
    {
        // Both windows share one applied palette and one edit draft.
        PackAppearance = QuickAppearance = new("工具窗口", SavePaletteAsync);
        Appearances = [QuickAppearance]; _selectedAppearance = QuickAppearance;
        ToggleEnabledCommand = new(_ => Track(SetEnabledAsync(!IsEnabled)));
        OpenQuickWindowCommand = new(_ => QuickWindowRequested?.Invoke(), () => IsEnabled && !_shuttingDown);
        OpenPackWindowCommand = new(_ => PackWindowRequested?.Invoke(), () => IsEnabled && !_shuttingDown);
        QuickPackCommand = new(_ => Track(CreateQuickTaskAsync()), () => CanEditDraft && Entries.Count > 0);
        BrowseQuickOutputCommand = new(_ =>
        {
            var path = SelectOutputDirectory is not null ? SelectOutputDirectory() : ChooseDirectory();
            if (!string.IsNullOrEmpty(path)) QuickOutputDirectory = path;
        });
        SettingsReady = LoadSettingsAsync();
    }
    private async Task LoadSettingsAsync()
    {
        try
        {
            var preferences = await Store.LoadPreferencesAsync();
            if (_disposed) return;
            _enabled = preferences.IsEnabled; _toggleShortcut = preferences.ToggleShortcut; _quickPack = preferences.QuickPack;
            _shortcutWindow = preferences.ShortcutWindow; _detailedWindowPlacement = preferences.DetailedWindowPlacement;
            _maximumParallel = preferences.MaximumParallel; _outputDirectory = preferences.LastOutputDirectory;
            _askOutputEachTime = preferences.AskOutputEachTime; _keepExternalWorkspace = preferences.KeepExternalWorkspace;
            _settingsColors = preferences.SettingsColors; _packColors = preferences.PackWindowColors; _quickColors = preferences.QuickWindowColors;
            // Migrate older independent colors by keeping the quick window's palette.
            // Keep both legacy fields until the next explicit save succeeds.
            QuickAppearance.Load(preferences.QuickWindowColors);
            Notice = string.Join(System.Environment.NewLine, Store.Diagnostics);
        }
        catch (Exception exception) when (IsExpected(exception)) { Notice = "文件集装箱设置未能读取。"; }
        finally { _settingsLoaded = true; Notify(""); RefreshCommands(); }
    }
    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        await SettingsReady;
        CancelIdleRelease();
        await _enabledGate.WaitAsync();
        try
        {
            if (_disposed || _shuttingDown) return false;
            if (enabled)
            {
                if (_enabled) return true;
                if (Manager.IsSuspended) await ReleaseRuntimeAsync();
                Manager.Resume();
                _activity.Dispose(); _activity = new();
                _enabled = true; _settingsDirty = true;
                Notify(nameof(IsEnabled)); DraftStateChanged();
                var saved = await SavePreferencesCoreAsync();
                if (_visible) await InitializeAsync();
                return saved;
            }
            _enabled = false; _settingsDirty = true;
            _activity.Cancel(); _collectionCancellation?.Cancel();
            Notify(nameof(IsEnabled)); DraftStateChanged();
            var preferencesSaved = await SavePreferencesCoreAsync();
            // Stop compression even when saving a draft fails; keep the draft and
            // the stopped records in memory until the save can be retried.
            while (_runtimeOperations.Count > 0)
            {
                try { await Task.WhenAll(_runtimeOperations.ToArray()); }
                catch (Exception exception) when (IsExpected(exception) || exception is OperationCanceledException) { }
                _runtimeOperations.RemoveWhere(task => task.IsCompleted);
            }
            await ReleaseRuntimeAsync();
            return preferencesSaved;
        }
        catch (Exception exception) when (IsExpected(exception))
        { RecordDiagnostic(exception); Notice = $"文件集装箱已停止，清单或记录未能保存，已保留在内存中：{exception.Message}"; return false; }
        finally { _enabledGate.Release(); }
    }
    public bool SetShortcut(KeyboardShortcut? shortcut, IEnumerable<KeyboardShortcut?> reserved)
    {
        if (!_settingsLoaded || _disposed || _shuttingDown) return false;
        if (shortcut is { IsValid: false }) { SetShortcutNotice("快捷键无效。"); return false; }
        if (shortcut is not null && reserved.Any(shortcut.Matches)) { SetShortcutNotice("此快捷键与其他工具冲突。"); return false; }
        SetShortcutNotice("");
        _toggleShortcut = shortcut; Notify(nameof(ToggleShortcut)); Notify(nameof(ShortcutText)); SavePreferences(); return true;
    }
    private FilePackPreferences CurrentPreferences() => new(MaximumParallel, OutputDirectory, AskOutputEachTime, KeepExternalWorkspace)
    {
        IsEnabled = IsEnabled, ToggleShortcut = ToggleShortcut, QuickPack = _quickPack,
        ShortcutWindow = ShortcutWindow, DetailedWindowPlacement = _detailedWindowPlacement,
        SettingsColors = _settingsColors, PackWindowColors = _packColors, QuickWindowColors = _quickColors
    };
    internal async Task<bool> SaveDetailedWindowPlacementAsync(FilePackWindowPlacement placement)
    {
        await SettingsReady;
        if (_disposed || placement.Normalize() is not { } normalized) return false;
        if (_detailedWindowPlacement == normalized && !_settingsDirty) return true;
        _detailedWindowPlacement = normalized; _settingsDirty = true;
        return await SavePreferencesCoreAsync();
    }
    private async Task<bool> SavePaletteAsync(ThemePalette palette)
    {
        await SettingsReady;
        await _preferencesGate.WaitAsync();
        try
        {
            if (_disposed) return false;
            var colors = FilePackAppearance.Colors(palette);
            var next = CurrentPreferences() with { PackWindowColors = colors, QuickWindowColors = colors };
            if (Manager.IsInitialized && !Manager.IsSuspended) await Manager.SavePreferencesAsync(next); else await Store.SavePreferencesAsync(next);
            // Commit persisted colors under the same gate as ordinary preferences;
            // a queued preference write must not race the appearance notification.
            _packColors = _quickColors = colors;
            return true;
        }
        catch (Exception exception) when (IsExpected(exception)) { Notice = "配色未能保存，请检查空间或权限。"; return false; }
        finally { _preferencesGate.Release(); }
    }
    internal void SetNotice(string notice) => Notice = notice;
    internal void SetShortcutNotice(string notice)
    {
        if (notice.Length > 0) Notice = notice;
        else if (Notice == _shortcutNotice) Notice = "";
        _shortcutNotice = notice;
    }
}
