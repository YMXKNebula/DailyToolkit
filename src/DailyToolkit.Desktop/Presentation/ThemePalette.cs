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
    public string ScrollThumb { get; init; } = "#267A5D";
    public string ScrollTrack { get; init; } = "#E8F2EC";
    public string SliderThumb { get; init; } = "#267A5D";
    public string SliderTrack { get; init; } = "#E8F2EC";
    public static ThemePalette Dark => new()
    {
        Window="#171D1B", Surface="#222B27", Sidebar="#1D2521", Text="#E6EEE9", Muted="#A6B7AD",
        Border="#39473F", Accent="#73D5AC", AccentSoft="#2B4437", AccentForeground="#15241C",
        Hover="#2B3730", Warning="#403620", WarningText="#F0D18D",
        ScrollThumb="#73D5AC",ScrollTrack="#2B4437",SliderThumb="#73D5AC",SliderTrack="#2B4437"
    };
    public static IReadOnlyList<ThemePreset> Presets =>
    [
        new("浅色",new()), new("深色",Dark),
        new("薄荷",new() { Window="#DDEFE4",Surface="#F3FBF5",Sidebar="#CFE6D9",Text="#203B31",Muted="#435F51",Border="#ACCDBB",
            Accent="#176045",AccentSoft="#C1E0CC",Hover="#D0E8D8" }),
        new("海蓝",new() { Window="#DAE7F5",Surface="#F4F8FD",Sidebar="#C9DDF1",Text="#23364D",Muted="#465C77",Border="#A8C4DF",
            Accent="#24548F",AccentSoft="#C4DAF1",Hover="#D5E5F7" }),
        new("薰衣草",new() { Window="#E7DDF3",Surface="#FAF6FE",Sidebar="#DCD0EE",Text="#352D45",Muted="#5E4F72",Border="#C3AEDB",
            Accent="#65438C",AccentSoft="#DCCCED",Hover="#E5D9F2" }),
        new("暖沙",new() { Window="#EFE2CA",Surface="#FFFAF1",Sidebar="#E4D4B7",Text="#43362C",Muted="#6B5947",Border="#CCB993",
            Accent="#75491F",AccentSoft="#E7D5B5",Hover="#EFE1C8",Warning="#FFF0D0",WarningText="#7A5626" }),
        new("玫瑰",new() { Window="#F5DCE5",Surface="#FFF5F8",Sidebar="#EFCBD9",Text="#462D37",Muted="#714757",Border="#D8A7BA",
            Accent="#913458",AccentSoft="#F0CBDA",Hover="#F5D9E3" }),
        new("石墨",new() { Window="#1C1D21",Surface="#26282D",Sidebar="#222429",Text="#F0F1F4",Muted="#B4B7C0",Border="#42454D",
            Accent="#8CAAF2",AccentSoft="#303D59",AccentForeground="#15223B",Hover="#30333B",Warning="#463B24",WarningText="#EED492" }),
        new("纯白",new() { Window="#F3F3F3",Surface="#FFFFFF",Sidebar="#FFFFFF",Text="#202020",Muted="#5C5C5C",Border="#D8D8D8",
            Accent="#333333",AccentSoft="#E3E3E3",Hover="#EBEBEB",Warning="#F3EDDA",WarningText="#695327" }),
        new("深海",new() { Window="#101D32",Surface="#172C47",Sidebar="#12233C",Text="#E5F2FF",Muted="#ACC4DE",Border="#345370",
            Accent="#78C9EF",AccentSoft="#233F5A",AccentForeground="#102A3C",Hover="#203A56",Warning="#443B24",WarningText="#F2D48E" }),
        new("暮紫",new() { Window="#22192F",Surface="#302340",Sidebar="#291D39",Text="#F3EAFE",Muted="#C5B1D8",Border="#554063",
            Accent="#D3A8F1",AccentSoft="#463057",AccentForeground="#322044",Hover="#3B2B4D",Warning="#49352B",WarningText="#F0C796" }),
        new("琥珀",new() { Window="#282019",Surface="#372C20",Sidebar="#2F251B",Text="#FFF0D7",Muted="#CDBA9C",Border="#5B4830",
            Accent="#F1BD6E",AccentSoft="#4C3A24",AccentForeground="#382613",Hover="#433323",Warning="#4D3C1C",WarningText="#F4D186" })
    ];
    public static string NormalizeColor(string? value,string fallback) =>
        LensBorderColor.TryParse(value,out var rgb) ? $"#{rgb:X6}" : fallback;
    public ThemePalette Normalize()
    {
        var defaults=new ThemePalette(); var result=this with { };
        foreach (var field in typeof(ThemePalette).GetProperties().Where(p => p.GetMethod is { IsStatic:false }))
            field.SetValue(result,NormalizeColor(field.GetValue(this) as string,field.Name switch
            { "ScrollThumb" or "SliderThumb" => NormalizeColor(Accent,defaults.Accent),
              "ScrollTrack" or "SliderTrack" => NormalizeColor(AccentSoft,defaults.AccentSoft),_ => (string)field.GetValue(defaults)! }));
        return result;
    }
}

public sealed class ThemePreset(string name,ThemePalette palette) : ObservableObject
{
    private bool _isSelected;
    public string Name { get; }=name;
    public ThemePalette Palette { get; }=palette with { ScrollThumb=palette.Accent,ScrollTrack=palette.AccentSoft,
        SliderThumb=palette.Accent,SliderTrack=palette.AccentSoft };
    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected,value); }
}

public sealed class ThemeColorOption(string key,string name,string value) : ObservableObject
{
    private string _value=value;
    public string Key { get; }=key;
    public string Name { get; }=name;
    public string Value { get => _value; set => Set(ref _value,ThemePalette.NormalizeColor(value,_value)); }
}
