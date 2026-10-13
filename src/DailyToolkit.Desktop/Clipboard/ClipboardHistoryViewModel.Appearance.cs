using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.ClipRecall;

public sealed partial class ClipboardHistoryViewModel
{
    // Reuse the application's palette type, preset definitions and color picker;
    // the applied values and editing draft belong ONLY to this tool's settings.
    private ThemePalette _appearance = new(), _appearanceDraft = new();
    private bool _updatingAppearance;
    private long _appearanceResetVersion;
    private Task<bool> _appearanceSave = Task.FromResult(true);
    internal Task<bool> PendingAppearanceSave => _appearanceSave;
    public ThemePalette Appearance => _appearance;
    public ThemePalette AppearanceDraft => _appearanceDraft;
    public ObservableCollection<ThemeColorOption> AppearanceColors { get; } = [];
    public IReadOnlyList<ThemePreset> AppearancePresets { get; } = ThemePalette.Presets;
    public RelayCommand SelectAppearancePresetCommand { get; private set; } = null!;
    public RelayCommand SaveAppearanceCommand { get; private set; } = null!;
    public RelayCommand DiscardAppearanceCommand { get; private set; } = null!;
    public RelayCommand ResetAppearanceCommand { get; private set; } = null!;
    public bool HasAppearanceChanges => AppearanceColors.Any(option => option.Value != (string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(Appearance)!);
    private void InitializeAppearance()
    {
        foreach (var (key, name) in new[] { ("Window", "面板背景"), ("Surface", "记录背景"), ("Text", "正文文字"),
            ("Muted", "摘要与时间"), ("Border", "边框"), ("Accent", "强调色"), ("AccentSoft", "选中背景"),
            ("AccentForeground", "强调按钮文字"), ("Hover", "悬停背景"), ("ScrollThumb", "滚动条滑块"), ("ScrollTrack", "滚动条轨道") })
        {
            var option = new ThemeColorOption(key, name, (string)typeof(ThemePalette).GetProperty(key)!.GetValue(_appearance)!);
            option.PropertyChanged += (_, e) =>
            {
                if (_updatingAppearance || e.PropertyName != nameof(ThemeColorOption.Value)) return;
                var next = _appearanceDraft with { }; typeof(ThemePalette).GetProperty(key)!.SetValue(next, option.Value);
                _appearanceDraft = next; NotifyAppearanceDraft();
            };
            AppearanceColors.Add(option);
        }
        SelectAppearancePresetCommand = new(value =>
        { if (value is ThemePreset preset && AppearancePresets.Contains(preset)) _ = SaveAppearancePaletteAsync(preset.Palette, preset: true); });
        SaveAppearanceCommand = new(_ => _ = SaveAppearanceAsync(), () => HasAppearanceChanges);
        DiscardAppearanceCommand = new(_ => SetAppearanceDraft(Appearance), () => HasAppearanceChanges);
        ResetAppearanceCommand = new(_ => _ = SaveAppearancePaletteAsync(new(), preset: true));
    }
    private ThemePalette ReadAppearance(Dictionary<string, string> colors)
    {
        var palette = new ThemePalette();
        foreach (var option in AppearanceColors)
            if (colors.TryGetValue(option.Key, out var color)) typeof(ThemePalette).GetProperty(option.Key)!.SetValue(palette,
                ThemePalette.NormalizeColor(color, (string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(palette)!));
        return palette.Normalize();
    }
    private void CommitAppearance(Dictionary<string, string> colors, bool resetDraft = false)
    {
        var next = ReadAppearance(colors);
        if (_appearance != next) { _appearance = next; Notify(nameof(Appearance)); }
        if (resetDraft) SetAppearanceDraft(_appearance);
        NotifyAppearanceDraft();
    }
    private void SetAppearanceDraft(ThemePalette palette)
    {
        _updatingAppearance = true;
        try
        {
            _appearanceDraft = palette.Normalize();
            foreach (var option in AppearanceColors) option.Value = (string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(_appearanceDraft)!;
        }
        finally { _updatingAppearance = false; }
        NotifyAppearanceDraft();
    }
    private void NotifyAppearanceDraft()
    {
        Notify(nameof(AppearanceDraft)); Notify(nameof(HasAppearanceChanges)); SaveAppearanceCommand.Refresh(); DiscardAppearanceCommand.Refresh();
        foreach (var preset in AppearancePresets) preset.IsSelected = AppearanceColors.All(option =>
            (string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(Appearance)! ==
            (string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(preset.Palette)!);
    }
    internal Task<bool> SaveAppearanceAsync() => SaveAppearancePaletteAsync(AppearanceDraft, preset: false);
    private Task<bool> SaveAppearancePaletteAsync(ThemePalette palette, bool preset)
    {
        if (_disposed || _stopping) return Task.FromResult(false);
        var operation = CommitAppearanceAsync(_appearanceSave, palette.Normalize(), preset, AppearanceDraft, _appearanceResetVersion);
        _appearanceSave = operation;
        return operation;
    }
    private async Task<bool> CommitAppearanceAsync(Task<bool> previous, ThemePalette palette, bool preset, ThemePalette requestedDraft, long resetVersion)
    {
        await previous; await Ready;
        await _settingsGate.WaitAsync();
        try
        {
            if (_disposed) return false;
            if (resetVersion != _appearanceResetVersion) return true;
            if (!_writable) { Notice = "配色未能保存，请检查原设置或恢复默认设置。"; return false; }
            var colors = AppearanceColors.ToDictionary(option => option.Key,
                option => (string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(palette)!, StringComparer.Ordinal);
            var before = _settings;
            var next = (before with { PanelColors = colors }).Normalize();
            await Store.SaveAsync(next);
            if (resetVersion != _appearanceResetVersion) { _settingsDirty = true; return true; }
            // Keep settings edited during the write; their existing flush will
            // persist them after this palette commit releases the same gate.
            if (_settings == before) { _settings = next; _settingsDirty = false; }
            else _settings = (_settings with { PanelColors = colors }).Normalize();
            var followingApplied = !HasAppearanceChanges;
            CommitAppearance(colors);
            if ((preset && (followingApplied || AppearanceDraft == requestedDraft)) || AppearanceDraft == palette) SetAppearanceDraft(Appearance);
            Notify(nameof(Settings)); Notice = "";
            SettingsCommitted?.Invoke(); QueueMaintenance();
            return true;
        }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        { Notice = "配色未能保存，请检查空间或权限。"; return false; }
        finally { _settingsGate.Release(); }
    }
    internal void ApplyAppearance(ResourceDictionary resources, bool draft = false)
    {
        var palette = draft ? AppearanceDraft : Appearance;
        foreach (var option in AppearanceColors)
        {
            Brush brush;
            if (SystemParameters.HighContrast)
                brush = option.Key switch { "Text" or "Muted" or "Border" => SystemColors.WindowTextBrush,
                    "Accent" or "ScrollThumb" => SystemColors.HighlightBrush, "AccentForeground" => SystemColors.HighlightTextBrush,
                    _ => SystemColors.WindowBrush };
            else { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(palette)!)); brush.Freeze(); }
            resources[option.Key + "Brush"] = brush;
        }
    }
}
