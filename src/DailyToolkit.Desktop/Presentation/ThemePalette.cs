using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Desktop.Presentation;

public sealed record ThemePalette
{
    public string Window { get; init; } = "#F5F7F6";
    public string Surface { get; init; } = "#FFFFFF";
    public string Sidebar { get; init; } = "#FFFFFF";
    public string Text { get; init; } = "#26372F";
    public string Muted { get; init; } = "#596B61";
    public string Border { get; init; } = "#E3E9E5";
    public string Accent { get; init; } = "#267A5D";
    public string AccentSoft { get; init; } = "#E8F2EC";
    public string AccentForeground { get; init; } = "#FFFFFF";
    public string Hover { get; init; } = "#F0F4F1";
    public string Warning { get; init; } = "#FFF5DE";
    public string WarningText { get; init; } = "#876823";
    public static ThemePalette Dark => new()
    {
        Window="#171D1B", Surface="#222B27", Sidebar="#1D2521", Text="#E6EEE9", Muted="#A6B7AD",
        Border="#39473F", Accent="#73D5AC", AccentSoft="#2B4437", AccentForeground="#15241C",
        Hover="#2B3730", Warning="#403620", WarningText="#F0D18D"
    };
    public static IReadOnlyList<ThemePreset> Presets =>
    [
        new("浅色",new()), new("深色",Dark),
        new("薄荷",new() { Window="#F0F8F5",Sidebar="#F5FCF8",Text="#203B31",Muted="#506E60",Border="#CFE4DA",
            Accent="#18745A",AccentSoft="#DDF0E7",Hover="#E8F5EE" }),
        new("海蓝",new() { Window="#F3F7FC",Sidebar="#F6F9FF",Text="#23364D",Muted="#566B85",Border="#D9E3F0",
            Accent="#2B62A4",AccentSoft="#E3EDFA",Hover="#ECF3FC" }),
        new("薰衣草",new() { Window="#F7F5FC",Sidebar="#FCFAFF",Text="#352D45",Muted="#6B5D7F",Border="#E5DDEC",
            Accent="#72509C",AccentSoft="#EDE4F8",Hover="#F1EBF8" }),
        new("暖沙",new() { Window="#FAF6EF",Surface="#FFFDFA",Sidebar="#FFFBF5",Text="#43362C",Muted="#786B5E",Border="#E9DFD1",
            Accent="#895B30",AccentSoft="#F1E6D4",Hover="#F5EDDF",Warning="#FFF0D0",WarningText="#7A5626" }),
        new("玫瑰",new() { Window="#FCF4F6",Sidebar="#FFF9FB",Text="#462D37",Muted="#7D5968",Border="#EBD8DF",
            Accent="#A24568",AccentSoft="#F6E2EA",Hover="#FBEAF0" }),
        new("石墨",new() { Window="#1C1D21",Surface="#26282D",Sidebar="#222429",Text="#F0F1F4",Muted="#B4B7C0",Border="#42454D",
            Accent="#8CAAF2",AccentSoft="#303D59",AccentForeground="#15223B",Hover="#30333B",Warning="#463B24",WarningText="#EED492" })
    ];
    public static string NormalizeColor(string? value,string fallback) =>
        LensBorderColor.TryParse(value,out var rgb) ? $"#{rgb:X6}" : fallback;
    public ThemePalette Normalize()
    {
        var defaults=new ThemePalette(); var result=this with { };
        foreach (var field in typeof(ThemePalette).GetProperties().Where(p => p.GetMethod is { IsStatic:false }))
            field.SetValue(result,NormalizeColor(field.GetValue(this) as string,(string)field.GetValue(defaults)!));
        return result;
    }
}

public sealed class ThemePreset(string name,ThemePalette palette) : ObservableObject
{
    private bool _isSelected;
    public string Name { get; }=name;
    public ThemePalette Palette { get; }=palette;
    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected,value); }
}

public sealed class ThemeColorOption(string key,string name,string value) : ObservableObject
{
    private string _value=value;
    public string Key { get; }=key;
    public string Name { get; }=name;
    public string Value { get => _value; set => Set(ref _value,ThemePalette.NormalizeColor(value,_value)); }
}
