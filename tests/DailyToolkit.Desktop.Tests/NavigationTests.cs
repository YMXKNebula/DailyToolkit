using System.IO;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static void CheckNavigation()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-navigation-tests-");
        try
        {
            var store=new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json"));
            Require(store.Save(["computer","future-tool","screen-lens"]),"Could not prepare saved navigation");
            var display=new DisplayInfo(1920,1080,1,1920,1040,false);
            MainViewModel Create() => new(new ControlledProbe(display),display,new LocalProbe(),
                favoritesStore:new FavoritesStore(Path.Combine(directory.FullName,"favorites.json")),
                gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")),navigationStore:store);
            using (var model=Create())
            {
                Require(model.Page == "computer" && model.NavigationItems.Select(item => item.Id).SequenceEqual(["computer","screen-lens"]) &&
                    model.NavigationItems[0].IsSelected,"Default direct navigation was wrong");
                Require(model.MoveNavigationItem("screen-lens","computer"),"Drag reorder was rejected");
                Require(model.NavigationItems[0].Id == "screen-lens" && model.Page == "computer","Sorting changed selection");
                Require(!model.MoveNavigationItem("unknown","computer") && !model.MoveNavigationItem("computer","computer"),"Invalid drag changed order");
                model.Gaming.Zoom=4;
                model.OpenFavoritesCommand.Execute(null);
                Require(model.IsFavorites && model.NavigationItems.Count == 0 && model.ShowFavoritesEmpty,"Favorites did not replace the sidebar");
                model.Gaming.IsFavorite=true;
                Require(model.NavigationItems.Single().Id == "screen-lens" && model.ShowScreenLens && model.Gaming.Zoom == 4 &&
                    !model.Gaming.IsActive,"Favorites did not share the existing tool state");
                model.ExitFavoritesCommand.Execute(null);
                Require(!model.IsFavorites && model.IsHome && model.NavigationItems.Count == 2 && model.Gaming.Zoom == 4,
                    "Exiting favorites did not restore normal navigation and selection");
                model.MoveDownCommand.Execute(model.NavigationItems[0]);
                Require(model.NavigationItems[0].Id == "computer","Context-menu reorder failed");
            }
            using (var reopened=Create())
            {
                Require(reopened.NavigationItems.Select(item => item.Id).SequenceEqual(["computer","screen-lens"]),"Order did not survive reopening");
                reopened.Page="screen-lens";
                reopened.OpenFavoritesCommand.Execute(null);
                reopened.Gaming.IsFavorite=false;
                Require(reopened.ShowFavoritesEmpty && reopened.NavigationItems.Count == 0,"Unstarring retained a selected favorite");
                reopened.ExitFavoritesCommand.Execute(null);
                Require(reopened.ShowScreenLens,"Exiting empty favorites did not restore the previously selected tool");
            }
            Require(store.Load().Contains("future-tool"),"Sorting deleted an unknown future tool identifier");
            File.WriteAllText(Path.Combine(directory.FullName,"navigation.json"),"{");
            Require(store.Load().Count == 0 && !store.Save(["../invalid"]),"Broken navigation did not recover safely");
            Console.WriteLine("PASS Direct navigation, sidebar replacement, exit, shared state and saved drag/context-menu ordering");
        }
        finally { directory.Delete(recursive:true); }
    }
}
