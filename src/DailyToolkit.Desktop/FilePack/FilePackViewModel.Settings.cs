using DailyToolkit.Core.FilePack;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.FilePack;

public sealed partial class FilePackViewModel
{
    private bool _enabled = true, _settingsLoaded, _settingsDirty;
    private KeyboardShortcut? _toggleShortcut = new(3, 0x50, "Ctrl + Alt + P");
    private QuickPackPreferences _quickPack = new();
    private Dictionary<string, string> _settingsColors = [], _packColors = [], _quickColors = [];
    private FilePackAppearance _selectedAppearance = null!;
    public Task SettingsReady { get; private set; } = Task.CompletedTask;
    public bool SettingsLoaded => _settingsLoaded;
    public bool IsEnabled => _enabled;
    public KeyboardShortcut? ToggleShortcut => _toggleShortcut;
    public string ShortcutText => ToggleShortcut?.Name ?? "未设置";
    public Action? QuickWindowRequested { get; set; }
    public Action? PackWindowRequested { get; set; }
    public RelayCommand ToggleEnabledCommand { get; private set; } = null!;
    public RelayCommand OpenQuickWindowCommand { get; private set; } = null!;
    public RelayCommand OpenPackWindowCommand { get; private set; } = null!;
    public RelayCommand QuickPackCommand { get; private set; } = null!;
    public RelayCommand BrowseQuickOutputCommand { get; private set; } = null!;
    public FilePackAppearance SettingsAppearance { get; private set; } = null!;
    public FilePackAppearance PackAppearance { get; private set; } = null!;
    public FilePackAppearance QuickAppearance { get; private set; } = null!;
    public IReadOnlyList<FilePackAppearance> Appearances { get; private set; } = [];
    public FilePackAppearance SelectedAppearance { get => _selectedAppearance; set => Set(ref _selectedAppearance, value); }
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
        SettingsAppearance = new("功能设置", palette => SavePaletteAsync(0, palette));
        PackAppearance = new("详细打包", palette => SavePaletteAsync(1, palette));
        QuickAppearance = new("快速装载", palette => SavePaletteAsync(2, palette));
        Appearances = [SettingsAppearance, PackAppearance, QuickAppearance]; _selectedAppearance = QuickAppearance;
        ToggleEnabledCommand = new(_ => Track(SetEnabledAsync(!IsEnabled)));
        OpenQuickWindowCommand = new(_ => QuickWindowRequested?.Invoke(), () => IsEnabled && !_shuttingDown);
        OpenPackWindowCommand = new(_ => PackWindowRequested?.Invoke());
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
            _maximumParallel = preferences.MaximumParallel; _outputDirectory = preferences.LastOutputDirectory;
            _askOutputEachTime = preferences.AskOutputEachTime; _keepExternalWorkspace = preferences.KeepExternalWorkspace;
            _settingsColors = preferences.SettingsColors; _packColors = preferences.PackWindowColors; _quickColors = preferences.QuickWindowColors;
            SettingsAppearance.Load(preferences.SettingsColors); PackAppearance.Load(preferences.PackWindowColors);
            QuickAppearance.Load(preferences.QuickWindowColors);
            Notice = string.Join(System.Environment.NewLine, Store.Diagnostics);
        }
        catch (Exception exception) when (IsExpected(exception)) { Notice = "文件集装箱设置未能读取。"; }
        finally { _settingsLoaded = true; Notify(""); RefreshCommands(); }
    }
    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        await SettingsReady;
        if (_disposed || _shuttingDown) return false;
        if (_enabled == enabled) return true;
        _enabled = enabled;
        if (!enabled) _collectionCancellation?.Cancel();
        Notify(nameof(IsEnabled)); DraftStateChanged();
        _settingsDirty = true;
        return await SavePreferencesCoreAsync();
    }
    public bool SetShortcut(KeyboardShortcut? shortcut, IEnumerable<KeyboardShortcut?> reserved)
    {
        if (!_settingsLoaded || _disposed || _shuttingDown) return false;
        if (shortcut is { IsValid: false }) { Notice = "快捷键无效。"; return false; }
        if (shortcut is not null && reserved.Any(shortcut.Matches)) { Notice = "此快捷键与其他工具冲突。"; return false; }
        _toggleShortcut = shortcut; Notify(nameof(ToggleShortcut)); Notify(nameof(ShortcutText)); SavePreferences(); return true;
    }
    private FilePackPreferences CurrentPreferences() => new(MaximumParallel, OutputDirectory, AskOutputEachTime, KeepExternalWorkspace)
    {
        IsEnabled = IsEnabled, ToggleShortcut = ToggleShortcut, QuickPack = _quickPack,
        SettingsColors = _settingsColors, PackWindowColors = _packColors, QuickWindowColors = _quickColors
    };
    private async Task<bool> SavePaletteAsync(int target, ThemePalette palette)
    {
        await SettingsReady;
        await _preferencesGate.WaitAsync();
        try
        {
            if (_disposed) return false;
            var colors = FilePackAppearance.Colors(palette);
            var next = CurrentPreferences();
            next = target switch { 0 => next with { SettingsColors = colors }, 1 => next with { PackWindowColors = colors },
                _ => next with { QuickWindowColors = colors } };
            if (_loaded) await Manager.SavePreferencesAsync(next); else await Store.SavePreferencesAsync(next);
            // Commit persisted colors under the same gate as ordinary preferences;
            // a queued preference write must not race the appearance notification.
            if (target == 0) _settingsColors = colors; else if (target == 1) _packColors = colors; else _quickColors = colors;
            return true;
        }
        catch (Exception exception) when (IsExpected(exception)) { Notice = "配色未能保存，请检查空间或权限。"; return false; }
        finally { _preferencesGate.Release(); }
    }
    internal void SetNotice(string notice) => Notice = notice;
}
