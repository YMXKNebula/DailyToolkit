using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
namespace DailyToolkit.Desktop.Presentation;
public sealed partial class MainViewModel
{
    public ObservableCollection<ThemeColorOption> ThemeColors { get; }=[];
    public IReadOnlyList<ThemePreset> ThemePresets { get; }=ThemePalette.Presets;
    public ThemePalette Theme => _appPreferences.Theme;
    private ThemePalette _previewTheme=new();
    public ThemePalette PreviewTheme => _previewTheme;
    public bool HasThemeChanges => PreviewTheme != Theme;
    public string AnimationColor
    {
        get => _appPreferences.AnimationColor;
        set { var color=ThemePalette.NormalizeColor(value,AnimationColor); if (color == AnimationColor) return;
            _appPreferences=_appPreferences with { AnimationColor=color }; SaveAppPreferences(); Notify(); }
    }
    public RelayCommand LightThemeCommand { get; private set; }=null!;
    public RelayCommand DarkThemeCommand { get; private set; }=null!;
    public RelayCommand ResetAppearanceCommand { get; private set; }=null!;
    public RelayCommand SelectThemePresetCommand { get; private set; }=null!;
    public RelayCommand SaveThemeCommand { get; private set; }=null!;
    public RelayCommand DiscardThemeCommand { get; private set; }=null!;
    public RelayCommand ResetThemePreviewCommand { get; private set; }=null!;
    private bool _updatingPalette;
    private void InitializeAppearance()
    {
        _previewTheme=Theme;
        var fields=new[] { ("Window","窗口背景"),("Surface","卡片与标题栏"),("Sidebar","侧栏背景"),("Text","主要文字"),
            ("Muted","次要文字"),("Border","边框"),("Accent","强调色"),("AccentSoft","选中背景"),
            ("AccentForeground","强调按钮文字"),("Hover","悬停背景"),("Warning","提示背景"),("WarningText","提示文字") };
        foreach (var (key,name) in fields)
        {
            var option=new ThemeColorOption(key,name,(string)typeof(ThemePalette).GetProperty(key)!.GetValue(Theme)!);
            option.PropertyChanged += (_,e) =>
            {
                if (_updatingPalette || e.PropertyName != nameof(ThemeColorOption.Value)) return;
                var palette=PreviewTheme with { }; typeof(ThemePalette).GetProperty(key)!.SetValue(palette,option.Value);
                _previewTheme=palette; NotifyThemePreview();
            };
            ThemeColors.Add(option);
        }
        LightThemeCommand=new(_ => SetTheme(new())); DarkThemeCommand=new(_ => SetTheme(ThemePalette.Dark));
        ResetAppearanceCommand=new(_ => { SetTheme(new()); AnimationColor="#267A5D"; PageAnimationsEnabled=true; });
        SelectThemePresetCommand=new(value => { if (value is ThemePreset preset && ThemePresets.Contains(preset)) SetTheme(preset.Palette); });
        SaveThemeCommand=new(_ => SetTheme(PreviewTheme),() => HasThemeChanges);
        DiscardThemeCommand=new(_ => SetThemePreview(Theme),() => HasThemeChanges);
        ResetThemePreviewCommand=new(_ => SetThemePreview(new()));
        UpdateSelectedPreset();
    }
    private void NotifyThemePreview()
    {
        Notify(nameof(PreviewTheme)); Notify(nameof(HasThemeChanges));
        SaveThemeCommand.Refresh(); DiscardThemeCommand.Refresh();
    }
    private void SetThemePreview(ThemePalette palette)
    {
        _updatingPalette=true;
        try { foreach (var option in ThemeColors) option.Value=(string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(palette)!; }
        finally { _updatingPalette=false; }
        _previewTheme=palette; NotifyThemePreview();
    }
    private void UpdateSelectedPreset()
    { foreach (var preset in ThemePresets) preset.IsSelected=preset.Palette == Theme; }
    private void SetTheme(ThemePalette palette)
    {
        var next=_appPreferences with { Theme=palette.Normalize() };
        if (!_appPreferencesStore.Save(next)) { Notice="配色未能保存。"; return; }
        _appPreferences=next; SetThemePreview(Theme); UpdateSelectedPreset(); Notify(nameof(Theme));
    }
    private bool SaveAppPreferences()
    {
        var saved=_appPreferencesStore.Save(_appPreferences); if (!saved) Notice="设置未能保存，退出后会丢失。"; return saved;
    }
    internal void ApplyTheme(ResourceDictionary resources)
    {
        foreach (var option in ThemeColors)
        {
            Brush brush;
            if (SystemParameters.HighContrast)
                brush=option.Key switch
                {
                    "Text" or "Muted" or "Border" or "WarningText" => SystemColors.WindowTextBrush,
                    "Accent" => SystemColors.HighlightBrush, "AccentForeground" => SystemColors.HighlightTextBrush,
                    _ => SystemColors.WindowBrush
                };
            else { brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(Theme)!)); brush.Freeze(); }
            resources[option.Key+"Brush"]=brush;
        }
        var animation=new SolidColorBrush((Color)ColorConverter.ConvertFromString(AnimationColor)); animation.Freeze();
        resources["AnimationBrush"]=SystemParameters.HighContrast ? SystemColors.HighlightBrush : animation;
    }
}
