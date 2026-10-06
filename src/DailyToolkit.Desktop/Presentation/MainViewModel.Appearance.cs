using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
namespace DailyToolkit.Desktop.Presentation;
public sealed partial class MainViewModel
{
    public ObservableCollection<ThemeColorOption> ThemeColors { get; }=[];
    public ThemePalette Theme => _appPreferences.Theme;
    public string AnimationColor
    {
        get => _appPreferences.AnimationColor;
        set { var color=ThemePalette.NormalizeColor(value,AnimationColor); if (color == AnimationColor) return;
            _appPreferences=_appPreferences with { AnimationColor=color }; SaveAppPreferences(); Notify(); }
    }
    public RelayCommand LightThemeCommand { get; private set; }=null!;
    public RelayCommand DarkThemeCommand { get; private set; }=null!;
    public RelayCommand ResetAppearanceCommand { get; private set; }=null!;
    private bool _updatingPalette;
    private void InitializeAppearance()
    {
        var fields=new[] { ("Window","窗口背景"),("Surface","卡片与标题栏"),("Sidebar","侧栏背景"),("Text","主要文字"),
            ("Muted","说明文字"),("Border","边框"),("Accent","强调色"),("AccentSoft","选中背景"),
            ("AccentForeground","强调按钮文字"),("Hover","悬停背景"),("Warning","提示背景"),("WarningText","提示文字") };
        foreach (var (key,name) in fields)
        {
            var option=new ThemeColorOption(key,name,(string)typeof(ThemePalette).GetProperty(key)!.GetValue(Theme)!);
            option.PropertyChanged += (_,e) =>
            {
                if (_updatingPalette || e.PropertyName != nameof(ThemeColorOption.Value)) return;
                var palette=Theme with { }; typeof(ThemePalette).GetProperty(key)!.SetValue(palette,option.Value);
                _appPreferences=_appPreferences with { Theme=palette }; SaveAppPreferences(); Notify(nameof(Theme));
            };
            ThemeColors.Add(option);
        }
        LightThemeCommand=new(_ => SetTheme(new())); DarkThemeCommand=new(_ => SetTheme(ThemePalette.Dark));
        ResetAppearanceCommand=new(_ => { SetTheme(new()); AnimationColor="#267A5D"; PageAnimationsEnabled=true; });
    }
    private void SetTheme(ThemePalette palette)
    {
        _updatingPalette=true;
        try { foreach (var option in ThemeColors) option.Value=(string)typeof(ThemePalette).GetProperty(option.Key)!.GetValue(palette)!; }
        finally { _updatingPalette=false; }
        _appPreferences=_appPreferences with { Theme=palette }; SaveAppPreferences(); Notify(nameof(Theme));
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
            else { brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(option.Value)); brush.Freeze(); }
            resources[option.Key+"Brush"]=brush;
        }
        var animation=new SolidColorBrush((Color)ColorConverter.ConvertFromString(AnimationColor)); animation.Freeze(); resources["AnimationBrush"]=animation;
    }
}
