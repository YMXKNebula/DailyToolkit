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

public sealed class ThemeColorOption(string key,string name,string value) : ObservableObject
{
    private string _value=value;
    public string Key { get; }=key;
    public string Name { get; }=name;
    public string Value { get => _value; set => Set(ref _value,ThemePalette.NormalizeColor(value,_value)); }
}
