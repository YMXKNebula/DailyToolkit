namespace DailyToolkit.Desktop.Presentation;

public sealed class NavigationItem(string id,string name,string icon) : ObservableObject
{
    private bool _selected;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Icon { get; } = icon;
    public bool IsSelected { get => _selected; internal set => Set(ref _selected,value); }
}
