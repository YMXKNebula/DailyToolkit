using System.ComponentModel;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private void OnNotesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(NotesViewModel.IsEnabled)) _navigation[1].IsEnabled = Notes.IsEnabled;
        if (e.PropertyName != nameof(NotesViewModel.IsFavorite)) { Notify(nameof(FooterStatusText)); return; }
        if (Notes.IsFavorite) _favoriteIds.Add("floating-notes"); else _favoriteIds.Remove("floating-notes");
        if (!_favoritesStore.Save(_favoriteIds)) Notice = "收藏未能保存，退出后会丢失。";
        RefreshNavigation();
        if (IsFavorites && !NavigationItems.Any(item => item.Id == Page)) _page = NavigationItems.FirstOrDefault()?.Id ?? "favorites-empty";
        NotifyNavigation(); RefreshCommand.Refresh();
    }
}
