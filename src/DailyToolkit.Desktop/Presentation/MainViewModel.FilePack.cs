using System.ComponentModel;
using DailyToolkit.Desktop.FilePack;

namespace DailyToolkit.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private void OnFilePackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(FilePackViewModel.IsEnabled)) _navigation[3].IsEnabled = FilePack.IsEnabled;
        if (e.PropertyName != nameof(FilePackViewModel.IsFavorite)) { Notify(nameof(FooterStatusText)); return; }
        if (FilePack.IsFavorite) _favoriteIds.Add("file-pack"); else _favoriteIds.Remove("file-pack");
        if (!_favoritesStore.Save(_favoriteIds)) Notice = "收藏未能保存，退出后会丢失。";
        RefreshNavigation();
        if (IsFavorites && !NavigationItems.Any(item => item.Id == Page)) _page = NavigationItems.FirstOrDefault()?.Id ?? "favorites-empty";
        NotifyNavigation(); RefreshCommand.Refresh();
    }
}
