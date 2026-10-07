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
    private string _previewAnimationColor="#267A5D";
    public ThemePalette PreviewTheme => _previewTheme;
    public bool HasThemeChanges => PreviewTheme != Theme || PreviewAnimationColor != AnimationColor;
    public string PreviewAnimationColor
    {
        get => _previewAnimationColor;
        set { var color=ThemePalette.NormalizeColor(value,PreviewAnimationColor);
            if (Set(ref _previewAnimationColor,color)) NotifyThemePreview(); }
    }
    public string AnimationColor
    {
        get => _appPreferences.AnimationColor;
        set { var color=ThemePalette.NormalizeColor(value,AnimationColor); if (color == AnimationColor) return;
            var followsApplied=PreviewAnimationColor == AnimationColor;
            _appPreferences=_appPreferences with { AnimationColor=color }; SaveAppPreferences(); Notify();
            if (followsApplied) _previewAnimationColor=color;
            NotifyThemePreview(); UpdateSelectedPreset(); }
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
        _previewAnimationColor=AnimationColor;
        var fields=new[] { ("Window","窗口背景"),("Surface","卡片与标题栏"),("Sidebar","侧栏背景"),("Text","主要文字"),
            ("Muted","次要文字"),("Border","边框"),("Accent","强调色"),("AccentSoft","选中背景"),
            ("AccentForeground","强调按钮文字"),("Hover","悬停背景"),("Warning","提示背景"),("WarningText","提示文字"),
            ("ScrollThumb","滚动条滑块"),("ScrollTrack","滚动条轨道"),("SliderThumb","调节滑块"),("SliderTrack","调节轨道") };
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
        LightThemeCommand=new(_ => SetTheme(new(),new ThemePalette().Accent));
        DarkThemeCommand=new(_ => SetTheme(ThemePalette.Dark,ThemePalette.Dark.Accent));
        ResetAppearanceCommand=new(_ => { LightThemeCommand.Execute(null); PageAnimationsEnabled=true; AnimationSpeed=1; });
        SelectThemePresetCommand=new(value => { if (value is ThemePreset preset && ThemePresets.Contains(preset)) SetTheme(preset.Palette,preset.Palette.Accent); });
        SaveThemeCommand=new(_ => SetTheme(PreviewTheme,PreviewAnimationColor),() => HasThemeChanges);
        DiscardThemeCommand=new(_ => SetThemePreview(Theme,AnimationColor),() => HasThemeChanges);
        ResetThemePreviewCommand=new(_ => SetThemePreview(new(),new ThemePalette().Accent));
        UpdateSelectedPreset();
    }
    private void NotifyThemePreview()
    {
        Notify(nameof(PreviewTheme)); Notify(nameof(PreviewAnimationColor)); Notify(nameof(HasThemeChanges));
        SaveThemeCommand.Refresh(); DiscardThemeCommand.Refresh();
    }
    private void SetThemePreview(ThemePalette palette,string animationColor)
    {
        _updatingPalette=true;
        try { foreach (var option in ThemeColors) option.Value=(string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(palette)!; }
        finally { _updatingPalette=false; }
        _previewTheme=palette; _previewAnimationColor=animationColor; NotifyThemePreview();
    }
    private void UpdateSelectedPreset()
    { foreach (var preset in ThemePresets) preset.IsSelected=preset.Palette == Theme && preset.Palette.Accent == AnimationColor; }
    private void SetTheme(ThemePalette palette,string animationColor)
    {
        var next=_appPreferences with { Theme=palette.Normalize(),AnimationColor=ThemePalette.NormalizeColor(animationColor,AnimationColor) };
        if (!_appPreferencesStore.Save(next)) { Notice="配色未能保存。"; return; }
        _appPreferences=next; SetThemePreview(Theme,AnimationColor); UpdateSelectedPreset(); Notify(nameof(Theme)); Notify(nameof(AnimationColor));
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
                    "Accent" or "ScrollThumb" or "SliderThumb" => SystemColors.HighlightBrush, "AccentForeground" => SystemColors.HighlightTextBrush,
                    _ => SystemColors.WindowBrush
                };
            else { brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(Theme)!)); brush.Freeze(); }
            resources[option.Key+"Brush"]=brush;
        }
        var animation=new SolidColorBrush((Color)ColorConverter.ConvertFromString(AnimationColor)); animation.Freeze();
        resources["AnimationBrush"]=SystemParameters.HighContrast ? SystemColors.HighlightBrush : animation;
    }
}
