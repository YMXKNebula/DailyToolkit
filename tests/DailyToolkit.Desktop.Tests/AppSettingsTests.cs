using System.IO;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static void CheckAppSettings()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-app-settings-tests-");
        try
        {
            var path=Path.Combine(directory.FullName,"settings.json");
            var store=new AppPreferencesStore(path);
            Require(store.Load().PageAnimationsEnabled,"Missing app settings did not enable animation by default");
            var display=new DisplayInfo(1920,1080,1,1920,1040,false);
            MainViewModel Create() => new(new ControlledProbe(display),display,new LocalProbe(),
                favoritesStore:new FavoritesStore(Path.Combine(directory.FullName,"favorites.json")),
                gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")),
                navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json")),appPreferencesStore:store);
            using (var model=Create())
            {
                model.Page="screen-lens"; model.Gaming.Zoom=4; model.Gaming.WheelZoomEnabled=false;
                model.OpenSettingsCommand.Execute(null);
                Require(model.IsSettings && !model.IsFavorites && !model.ShowFavoritesEmpty && !model.ShowRefresh &&
                    model.PageTitle == "主题配色" && model.NavigationItems.Count == 5 &&
                    model.NavigationItems.Single(item => item.IsSelected).Id == "settings-theme" &&
                    !model.RefreshCommand.CanExecute(null) && !model.MoveNavigationItem("settings","computer") &&
                    model.Gaming.Zoom == 4 && !model.Gaming.IsActive,"Opening settings changed tool state or kept unrelated navigation");
                model.ExitSettingsCommand.Execute(null);
                Require(model.ShowScreenLens && model.NavigationItems.Count == 2,"Closing settings lost the previous tool");
                model.OpenFavoritesCommand.Execute(null);
                Require(model.ShowFavoritesEmpty,"The check did not start with empty favorites");
                model.OpenSettingsCommand.Execute(null);
                Require(model.IsSettings && !model.ShowFavoritesEmpty && !model.ShowAllTools && model.SettingsToggleHint == "返回收藏夹",
                    "Settings displayed the empty-favorites card or forgot its return destination");
                model.Gaming.IsFavorite=true;
                model.Page="settings-startup";
                Require(model.ShowStartupSettings && !model.ShowThemeSettings && model.NavigationItems.Single(item => item.IsSelected).Id == "settings-startup",
                    "Selecting a settings category exited settings or lost its selection");
                Require(model.IsSettings && model.NavigationItems.Count == 5,"Updating a favorite replaced the open settings page");
                model.ExitSettingsCommand.Execute(null);
                Require(model.IsFavorites && model.ShowScreenLens,"Returning from settings lost the favorites context");
                model.OpenSettingsCommand.Execute(null);
                model.Gaming.IsFavorite=false;
                model.Page="favorites";
                Require(model.IsFavorites && model.ShowFavoritesEmpty && !model.IsSettings,"The star could not exit settings after the last favorite was removed");
                model.OpenSettingsCommand.Execute(null);
                model.Page="computer";
                Require(model.IsHome && !model.IsSettings && !model.IsFavorites,"Direct navigation did not leave settings and favorites correctly");
                model.PageAnimationsEnabled=false;
            }
            using (var reopened=Create()) Require(!reopened.PageAnimationsEnabled && !reopened.Gaming.WheelZoomEnabled,
                "App animation preference or independent tool settings did not survive restart");
            Require(!Directory.GetFiles(directory.FullName,"*.tmp").Any(),"Saving app preferences left temporary files");
            File.WriteAllText(path,"{");
            Require(store.Load().PageAnimationsEnabled,"Broken settings did not recover to defaults");
            File.WriteAllText(path,new string('x',16385));
            Require(store.Load().PageAnimationsEnabled,"Oversized settings did not recover to defaults");
            var blocked=Path.Combine(directory.FullName,"blocked");
            File.WriteAllText(blocked,"file");
            Require(!new AppPreferencesStore(Path.Combine(blocked,"settings.json")).Save(new()),"A failed settings write reported success");
            Console.WriteLine("PASS Software settings restore tool/favorites context, save animation preference and recover safely from unreadable files");
        }
        finally
        {
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-app-settings-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }
}
