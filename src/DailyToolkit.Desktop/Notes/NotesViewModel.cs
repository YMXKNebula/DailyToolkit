using System.Windows.Media;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Core.Notes;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Notes;

public sealed record NotesPreset<T>(string Name, T Value);
public sealed record NotesColors(string Background, string Text, string Border);

public sealed partial class NotesViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _saveTimer;
    private readonly SemaphoreSlim _saving = new(1, 1);
    private NotesPreferences _preferences = new();
    private string _text = "", _notice = "";
    private bool _contentDirty, _settingsDirty, _settingsWritable = true, _visible, _focused, _favorite, _disposed, _loaded;
    private long _contentRevision, _settingsRevision;
    private bool _contentLoaded;
    private Task? _contentLoadTask;
    internal bool IsContentLoaded => _contentLoaded;
    internal bool IsSaveScheduled => _saveTimer.IsEnabled;
    internal bool IsIdleForMemoryCollection => !_disposed && _loaded && !IsEnabled && !_visible && !_focused &&
        !_saveTimer.IsEnabled && _saving.CurrentCount == 1 && !_contentDirty && !_settingsDirty &&
        (_contentLoadTask is null || _contentLoadTask.IsCompletedSuccessfully) && Ready.IsCompletedSuccessfully &&
        PendingColorSave.IsCompletedSuccessfully;
    public NotesStore Store { get; }
    public Task Ready { get; }
    public bool ContentWritable { get; private set; } = true;
    public bool IsLoaded => _loaded;
    public NotesPreferences Preferences => _preferences;
    public IReadOnlyList<NotesPreset<NotesColors>> AppearancePresets { get; } =
    [new("纸白", new("#FAFAF7", "#28313D", "#267A5D")),
     new("暖黄", new("#FFF0AA", "#493510", "#9C6600")),
     new("浅蓝", new("#DCEEFF", "#173C63", "#246DB5")),
     new("薄荷", new("#DDF1DF", "#19432D", "#267A5D")),
     new("淡粉", new("#FFE3EC", "#65213A", "#AE3D6C")),
     new("淡紫", new("#EDE3FF", "#3E245D", "#7950B6")),
     new("沙色", new("#EDE2D0", "#463625", "#8C642E")),
     new("石墨", new("#30343C", "#F3F5F7", "#91B8FF")),
     new("夜蓝", new("#142B43", "#E2F2FF", "#69C6FF")),
     new("墨绿", new("#183A30", "#ECF8E8", "#83D9A4"))];
    public IReadOnlyList<NotesPreset<NotesBackground>> BackgroundPresets { get; } =
    [new("默认浅色", new()), new("便签黄", new("#FFF0AA")), new("浅蓝", new("#DCEEFF")),
        new("浅绿", new("#DDF1DF")), new("深灰", new("#30343C")), new("半透明黑", new("#101216", .65))];
    public IReadOnlyList<NotesPreset<NotesFont>> FontPresets { get; } =
    [new("默认正文", new()), new("大字", new(Size: 24)), new("粗体", new(Bold: true)),
        new("等宽文字", new("Consolas", Size: 18))];
    private IReadOnlyList<string> _installedFonts = [];
    public IReadOnlyList<string> InstalledFonts => _installedFonts;
    public void LoadFontList()
    {
        if (_disposed || !IsEnabled || _installedFonts.Count > 0) return;
        _installedFonts = Fonts.SystemFontFamilies.Select(font => font.Source).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        Notify(nameof(InstalledFonts));
    }
    internal void ReleaseFontList()
    {
        if (_installedFonts.Count == 0) return;
        _installedFonts = []; Notify(nameof(InstalledFonts));
    }

    public NotesViewModel(NotesStore? store = null)
    {
        Store = store ?? new();
        ToggleFavoriteCommand = new(_ => IsFavorite = !IsFavorite);
        SaveColorCommand = new(_ => _ = SaveColorDraftAsync(), () => HasColorChanges);
        DiscardColorCommand = new(_ => DiscardColorDraft(), () => HasColorChanges);
        _saveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += SaveTick;
        Ready = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var result = await Store.LoadAsync();
        if (_disposed) return;
        _preferences = result.Preferences; _text = result.Document.Text; ContentWritable = result.ContentWritable; _contentLoaded = true;
        _settingsWritable = result.SettingsWritable; _loaded = true; Notice = result.Notice; Notify("");
    }

    public string Text
    {
        get => _text;
        set { if (!_loaded || !_contentLoaded || !ContentWritable || !Set(ref _text, value)) return; _contentDirty = true; _contentRevision++; ScheduleSave(); }
    }
    internal Task EnsureContentLoadedAsync()
    {
        if (_contentLoaded || _disposed) return Task.CompletedTask;
        return _contentLoadTask ??= ReloadContentAsync();
    }
    private async Task ReloadContentAsync()
    {
        try
        {
            await Ready;
            var result = await Store.LoadContentAsync();
            if (_disposed) return;
            _text = result.Text; ContentWritable = result.ContentWritable; _contentLoaded = true;
            if (result.Notice.Length > 0) Notice = result.Notice;
            Notify(nameof(Text)); Notify(nameof(ContentWritable));
        }
        finally { _contentLoadTask = null; }
    }
    // Only clean, hidden content can be dropped. Failed saves retain the live document.
    internal bool ReleaseInactiveContent()
    {
        if (IsVisible || _contentDirty || _saving.CurrentCount == 0 || _contentLoadTask is { IsCompleted: false }) return false;
        _text = ""; _contentLoaded = false; _contentLoadTask = null;
        Notify(nameof(Text));
        if (!IsEnabled) ReleaseFontList();
        return true;
    }
    public string Notice { get => _notice; set => Set(ref _notice, value); }
    public bool IsVisible { get => _visible; internal set { if (Set(ref _visible, value)) Notify(nameof(Status)); } }
    public bool IsFocused { get => _focused; internal set { if (Set(ref _focused, value)) Notify(nameof(Status)); } }
    public bool IsFavorite { get => _favorite; set { if (Set(ref _favorite, value)) { Notify(nameof(FavoriteSymbol)); Notify(nameof(FavoriteHint)); } } }
    public string FavoriteSymbol => IsFavorite ? "★" : "☆";
    public string FavoriteHint => IsFavorite ? "取消收藏" : "加入收藏夹";
    public RelayCommand ToggleFavoriteCommand { get; }
    public string Status => !IsEnabled ? "已停用" : !IsVisible ? "已隐藏" : IsFocused ? "正在编辑" : "显示中 · 未聚焦";
    public bool IsEnabled { get => _preferences.IsEnabled; set => Update(_preferences with { IsEnabled = value }); }
    public bool Topmost { get => _preferences.Topmost; set => Update(_preferences with { Topmost = value }); }
    public bool FocusOnClick { get => _preferences.FocusOnClick; set => Update(_preferences with { FocusOnClick = value }); }
    public bool AllowManualResize { get => _preferences.AllowManualResize; set => Update(_preferences with { AllowManualResize = value }); }
    public bool OpaqueWhenFocused { get => _preferences.OpaqueWhenFocused; set => Update(_preferences with { OpaqueWhenFocused = value }); }
    public string FocusBorderColor { get => _preferences.FocusBorderColor;
        set { if (NotesPreferences.ValidColor(value)) Update(_preferences with { FocusBorderColor = value }); } }
    public bool Movable { get => _preferences.PositionMode == NotesPositionMode.Movable;
        set => Update(_preferences with { PositionMode = value ? NotesPositionMode.Movable : NotesPositionMode.Fixed }); }
    public bool IsFixedMode { get => !Movable; set { if (value) Movable = false; } }
    public bool IsMovableMode { get => Movable; set { if (value) Movable = true; } }
    public string ToggleShortcutText => _preferences.ToggleShortcut?.Name ?? "未设置";
    public string FocusShortcutText => _preferences.FocusShortcut?.Name ?? "未设置";
    public double Width { get => _preferences.Width; set => Update(_preferences with { Width = value }); }
    public double Height { get => _preferences.Height; set => Update(_preferences with { Height = value }); }
    public string BackgroundColor { get => _preferences.Background.Color;
        set { if (NotesPreferences.ValidColor(value)) Update(_preferences with { Background = _preferences.Background with { Color = value } }); } }
    public double BackgroundOpacity { get => _preferences.Background.Opacity;
        set => Update(_preferences with { Background = _preferences.Background with { Opacity = value } }); }
    public double Radius { get => _preferences.Background.Radius;
        set => Update(_preferences with { Background = _preferences.Background with { Radius = value } }); }
    public string FontColor { get => _preferences.Font.Color;
        set { if (NotesPreferences.ValidColor(value)) Update(_preferences with { Font = _preferences.Font with { Color = value } }); } }
    public double FontOpacity { get => _preferences.Font.Opacity;
        set => Update(_preferences with { Font = _preferences.Font with { Opacity = value } }); }
    public double FontSize { get => _preferences.Font.Size;
        set => Update(_preferences with { Font = _preferences.Font with { Size = value } }); }
    public string FontFamily { get => _preferences.Font.Family;
        set { if (!string.IsNullOrWhiteSpace(value)) Update(_preferences with { Font = _preferences.Font with { Family = value } }); } }
    public bool FontBold { get => _preferences.Font.Bold;
        set => Update(_preferences with { Font = _preferences.Font with { Bold = value } }); }

    public void Update(NotesPreferences preferences)
    {
        if (_disposed || !_loaded) return;
        preferences = preferences.Normalize();
        if (preferences == _preferences) return;
        var beforeColors = ReadColors(_preferences); var nextColors = ReadColors(preferences);
        if (beforeColors.Background != nextColors.Background) _backgroundColorRevision++;
        if (beforeColors.Text != nextColors.Text) _textColorRevision++;
        if (beforeColors.Border != nextColors.Border) _borderColorRevision++;
        if (_colorDraft is { } draft) _colorDraft = new(
            beforeColors.Background != nextColors.Background ? nextColors.Background : draft.Background,
            beforeColors.Text != nextColors.Text ? nextColors.Text : draft.Text,
            beforeColors.Border != nextColors.Border ? nextColors.Border : draft.Border);
        _preferences = preferences; _settingsDirty = true; _settingsRevision++;
        if (beforeColors != nextColors) NotifyColorDraft();
        Notify(""); ScheduleSave();
    }
    public void ApplyBackground(NotesBackground background) => Update(_preferences with { Background = background });
    public void ApplyAppearance(NotesColors colors) => Update(_preferences with
    {
        Background = _preferences.Background with { Color = colors.Background },
        Font = _preferences.Font with { Color = colors.Text }, FocusBorderColor = colors.Border
    });
    public void ApplyFont(NotesFont font) => Update(_preferences with { Font = font with { Color = FontColor, Opacity = FontOpacity } });
    public void ResetBackground()
    { _backgroundColorRevision++; ChangeColorDraft(PreviewColors with { Background = new NotesBackground().Color }); ApplyBackground(new()); }
    public void ResetFont()
    { _textColorRevision++; ChangeColorDraft(PreviewColors with { Text = new NotesFont().Color }); Update(_preferences with { Font = new() }); }
    public void ResetPosition() => Update(_preferences with { X = null, Y = null });
    public void ResetSettings()
    {
        _backgroundColorRevision++; _textColorRevision++; _borderColorRevision++;
        DiscardColorDraft();
        _settingsWritable = true; _settingsDirty = true; _settingsRevision++;
        Update(new()); ScheduleSave();
    }
    public bool SetShortcut(bool focus, KeyboardShortcut? shortcut, KeyboardShortcut? lensShortcut)
    {
        if (shortcut is { IsValid: false }) { Notice = "请选择有效的快捷键。"; return false; }
        if (shortcut is not null && (shortcut.Matches(focus ? _preferences.ToggleShortcut : _preferences.FocusShortcut) || shortcut.Matches(lensShortcut)))
        { Notice = "此快捷键与浮笺的另一快捷键或放大镜冲突。"; return false; }
        Update(focus ? _preferences with { FocusShortcut = shortcut } : _preferences with { ToggleShortcut = shortcut });
        Notice = ""; return true;
    }
    public bool ResetShortcuts(KeyboardShortcut? lensShortcut)
    {
        var defaults = new NotesPreferences();
        if (defaults.ToggleShortcut!.Matches(lensShortcut) || defaults.FocusShortcut!.Matches(lensShortcut))
        { Notice = "默认浮笺快捷键与放大镜冲突，请先修改该快捷键。"; return false; }
        Update(_preferences with { ToggleShortcut = defaults.ToggleShortcut, FocusShortcut = defaults.FocusShortcut }); return true;
    }

    private void ScheduleSave() { if (!_disposed && (_contentDirty || _settingsDirty)) { _saveTimer.Stop(); _saveTimer.Start(); } }
    private async void SaveTick(object? sender, EventArgs e) { _saveTimer.Stop(); await FlushAsync(); }
    public async Task<bool> FlushAsync(bool exiting = false)
    {
        var pendingColor = PendingColorSave; var waitingForColor = exiting && !pendingColor.IsCompleted;
        await Ready; var colorSaved = await pendingColor;
        if (waitingForColor && !colorSaved) return false;
        _saveTimer.Stop();
        await _saving.WaitAsync();
        try
        {
            while (_contentDirty && ContentWritable)
            {
                var revision = _contentRevision; var text = Text;
                await Store.SaveContentAsync(text);
                if (_contentRevision == revision) _contentDirty = false;
            }
            while (_settingsDirty)
            {
                if (!_settingsWritable) { Notice = "原设置无法读取，请恢复默认设置后重试。正文仍可保存。"; return false; }
                var revision = _settingsRevision; var settings = Preferences;
                await Store.SaveSettingsAsync(settings);
                if (_settingsRevision == revision) _settingsDirty = false;
            }
            if (!ContentWritable) return !_contentDirty;
            if (!_contentDirty && !_settingsDirty && Notice.StartsWith("保存失败", StringComparison.Ordinal)) Notice = "";
            return !_contentDirty && !_settingsDirty;
        }
        catch (Exception exception) when (NotesStore.IsStorageError(exception))
        { DiagnosticReportService.Shared.RecordError(DiagnosticModule.FloatingNotes, exception); Notice = "保存失败，请检查空间或权限。重试保存或导出后再退出。"; return false; }
        finally { _saving.Release(); }
    }
    public void Dispose() { _disposed = true; _saveTimer.Stop(); _saveTimer.Tick -= SaveTick; ReleaseFontList(); }
}
