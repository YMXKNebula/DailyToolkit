namespace DailyToolkit.Desktop.Presentation;

public sealed class NavigationItem(string id,string name,string icon,bool canToggle=false) : ObservableObject
{
    private bool _selected;
    private bool _enabled=true;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Icon { get; } = icon;
    public bool CanToggle { get; }=canToggle;
    public bool IsEnabled { get => _enabled; internal set { if (Set(ref _enabled,value)) Notify(nameof(EnableHint)); } }
    public string EnableHint => IsEnabled ? "停用功能" : "启用功能";
    public bool IsSelected { get => _selected; internal set => Set(ref _selected,value); }
}
