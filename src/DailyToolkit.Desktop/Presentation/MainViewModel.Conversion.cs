using System.ComponentModel;
using DailyToolkit.Desktop.Conversion;

namespace DailyToolkit.Desktop.Presentation;

public sealed partial class MainViewModel
{
    private void OnConversionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(ConversionViewModel.IsEnabled)) _navigation[4].IsEnabled = Conversion.IsEnabled;
        if (e.PropertyName != nameof(ConversionViewModel.IsFavorite)) { Notify(nameof(FooterStatusText)); return; }
        if (Conversion.IsFavorite) _favoriteIds.Add("format-conversion"); else _favoriteIds.Remove("format-conversion");
        if (!_favoritesStore.Save(_favoriteIds)) Notice = "收藏未能保存，退出后会丢失。";
        RefreshNavigation();
        if (IsFavorites && !NavigationItems.Any(item => item.Id == Page)) _page = NavigationItems.FirstOrDefault()?.Id ?? "favorites-empty";
        NotifyNavigation(); RefreshCommand.Refresh();
    }
}
