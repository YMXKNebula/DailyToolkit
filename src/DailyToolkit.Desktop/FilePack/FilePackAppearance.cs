using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.FilePack;

// FilePack owns only its palettes and edit drafts; presets and controls are shared with the other tools.
public sealed class FilePackAppearance : ObservableObject
{
    private readonly Func<ThemePalette, Task<bool>> _save;
    private ThemePalette _applied = new(), _draft = new();
    private bool _updating;
    public string Name { get; }
    public ThemePalette Applied => _applied;
    public ThemePalette Draft => _draft;
    public bool HasChanges => Applied != Draft;
    public ObservableCollection<ThemeColorOption> Options { get; } = [];
    public IReadOnlyList<ThemePreset> Presets { get; } = ThemePalette.Presets;
    public RelayCommand PresetCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public Task PendingSave { get; private set; } = Task.CompletedTask;
    public FilePackAppearance(string name, Func<ThemePalette, Task<bool>> save)
    {
        Name = name; _save = save;
        foreach (var (key, label) in new[] { ("Window", "窗口背景"), ("Surface", "卡片背景"), ("Text", "主要文字"),
            ("Muted", "次要文字"), ("Border", "边框"), ("Accent", "强调色"), ("AccentSoft", "选中背景"),
            ("AccentForeground", "强调按钮文字"), ("Hover", "悬停背景"), ("Warning", "提示背景"),
            ("WarningText", "提示文字"), ("ScrollThumb", "滚动条滑块"), ("ScrollTrack", "滚动条轨道") })
        {
            var property = typeof(ThemePalette).GetProperty(key)!;
            var option = new ThemeColorOption(key, label, (string)property.GetValue(_draft)!);
            option.PropertyChanged += (_, e) =>
            {
                if (_updating || e.PropertyName != nameof(ThemeColorOption.Value)) return;
                var next = Draft with { }; property.SetValue(next, option.Value); _draft = next; Changed();
            };
            Options.Add(option);
        }
        PresetCommand = new(value => { if (value is ThemePreset preset && Presets.Contains(preset)) QueueSave(preset.Palette, true); });
        SaveCommand = new(_ => QueueSave(Draft, false), () => HasChanges);
        DiscardCommand = new(_ => SetDraft(Applied), () => HasChanges);
    }
    internal void Load(Dictionary<string, string> colors)
    {
        var palette = new ThemePalette();
        foreach (var property in typeof(ThemePalette).GetProperties().Where(property => property.GetMethod is { IsStatic: false }))
            if (colors.TryGetValue(property.Name, out var color)) property.SetValue(palette,
                ThemePalette.NormalizeColor(color, (string)property.GetValue(palette)!));
        _applied = palette.Normalize(); SetDraft(Applied); Notify(nameof(Applied));
    }
    private void QueueSave(ThemePalette palette, bool preset) => PendingSave = CommitAsync(PendingSave, palette.Normalize(), Draft, preset);
    private async Task CommitAsync(Task previous, ThemePalette palette, ThemePalette requestedDraft, bool preset)
    {
        await previous;
        if (!await _save(palette)) return;
        var followed = !HasChanges;
        _applied = palette; Notify(nameof(Applied));
        if (preset && (followed || Draft == requestedDraft) || Draft == palette) SetDraft(palette);
        Changed();
    }
    private void SetDraft(ThemePalette palette)
    {
        _updating = true;
        try { _draft = palette; foreach (var option in Options) option.Value = (string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(palette)!; }
        finally { _updating = false; }
        Changed();
    }
    private void Changed()
    {
        Notify(nameof(Draft)); Notify(nameof(HasChanges)); SaveCommand?.Refresh(); DiscardCommand?.Refresh();
        foreach (var preset in Presets) preset.IsSelected = preset.Palette == Applied;
    }
    internal Dictionary<string, string> Colors() => Colors(Applied);
    internal static Dictionary<string, string> Colors(ThemePalette palette) => typeof(ThemePalette).GetProperties()
        .Where(property => property.GetMethod is { IsStatic: false }).ToDictionary(property => property.Name, property => (string)property.GetValue(palette)!);
    internal void Apply(ResourceDictionary resources, bool draft = false)
    {
        foreach (var pair in Colors(draft ? Draft : Applied))
        {
            Brush brush = SystemParameters.HighContrast ? pair.Key switch
            {
                "Text" or "Muted" or "Border" => SystemColors.WindowTextBrush,
                "Accent" or "ScrollThumb" => SystemColors.HighlightBrush, "AccentForeground" => SystemColors.HighlightTextBrush,
                _ => SystemColors.WindowBrush
            } : new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            if (brush.CanFreeze) brush.Freeze(); resources[pair.Key + "Brush"] = brush;
        }
    }
}
