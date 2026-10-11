using System.ComponentModel;
using DailyToolkit.Desktop.ClipRecall;

namespace DailyToolkit.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private void OnClipRecallChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(ClipboardHistoryViewModel.IsEnabled)) _navigation[2].IsEnabled = ClipRecall.IsEnabled;
        if (e.PropertyName != nameof(ClipboardHistoryViewModel.IsFavorite)) { Notify(nameof(FooterStatusText)); return; }
        if (ClipRecall.IsFavorite) _favoriteIds.Add("clip-recall"); else _favoriteIds.Remove("clip-recall");
        if (!_favoritesStore.Save(_favoriteIds)) Notice = "收藏未能保存，退出后会丢失。";
        RefreshNavigation();
        if (IsFavorites && !NavigationItems.Any(item => item.Id == Page)) _page = NavigationItems.FirstOrDefault()?.Id ?? "favorites-empty";
        NotifyNavigation(); RefreshCommand.Refresh();
    }
}
