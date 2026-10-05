using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop;
using DailyToolkit.Desktop.Environment;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static void CheckLensPointer()
    {
        PixelBounds? bounds=new(10,20,320,192);
        var moved=(0,0); var wheel=0;
        var canMove=true; var ended=0; var canZoom=true;
        using var controller=new LensPointerController(() => bounds,(x,y) => moved=(x,y),delta => wheel+=delta,install:false,
            canMove:() => canMove,dragEnded:() => ended++,canZoom:() => canZoom);
        Require(!controller.Process(0x201,0,0),"Clicks outside the lens were intercepted");
        Require(!controller.Process(0x20A,0,0,120),"Wheel outside the lens was intercepted");
        Require(controller.Process(0x201,170,116) && controller.Process(0x200,210,176) && moved == (50,80),
            "Dragging from the magnified picture's center did not preserve its pointer offset");
        Require(controller.Process(0x202,210,176),"Picture drag did not end");
        Require(controller.Process(0x201,12,22) && controller.Process(0x200,52,82) && moved == (50,80),"Border dragging lost its pointer offset");
        Require(controller.Process(0x202,900,900),"Drag release outside the frame was not consumed");
        Require(controller.Process(0x20A,100,100,120) && wheel == 120,"Wheel inside the lens did not adjust zoom");
        canZoom=false;
        Require(!controller.Process(0x20A,100,100,120) && wheel == 120,"Disabled wheel zoom still intercepted or changed zoom");
        canZoom=true;
        Require(controller.Process(0x201,12,22),"Second drag did not begin");
        canMove=false;
        Require(!controller.Process(0x200,52,82) && controller.Process(0x202,52,82) && ended == 3,
            "Changing to fixed during a drag lost release routing or kept dragging");
        Require(!controller.Process(0x201,12,22) && !controller.Process(0x201,170,116) &&
            controller.Process(0x20A,100,100,120) && wheel == 240,
            "Fixed mode intercepted clicks or disabled wheel zoom");
        canMove=true;
        Require(controller.Process(0x201,12,22),"Returning to movable did not allow dragging");
        bounds=null;
        Require(!controller.Process(0x200,60,60) && controller.Process(0x202,60,60) &&
            !controller.Process(0x20A,100,100,120),"Hiding retained a drag or intercepted unrelated input");
        using var gaming=new GamingViewModel(new GamingPreferencesStore(
            Path.Combine(Path.GetTempPath(),$"DailyToolkit-pointer-{Guid.NewGuid():N}.json")));
        gaming.FrameWidth=800; gaming.FrameHeight=240;
        Require(gaming.FrameSizeText == "800 × 240 像素","Changing width changed the height");
        for (var i=0;i<100;i++) gaming.AdjustZoom(120);
        Require(gaming.Zoom == 10 && !gaming.IsActive,"Wheel upper limit changed activation state");
        for (var i=0;i<100;i++) gaming.AdjustZoom(-120);
        Require(gaming.Zoom == 1 && !gaming.IsActive,"Wheel lower limit changed activation state");
        Console.WriteLine("PASS Movable lenses drag from the picture or border; fixed lenses pass clicks through; wheel routing and cleanup stay safe");
    }

    private static void CheckFavorites()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-favorites-tests-");
        var path=Path.Combine(directory.FullName,"favorites.json");
        try
        {
            var store=new FavoritesStore(path);
            Require(store.Load().Count==0 && store.Save(["future-tool"]),"Missing favorites did not start empty");
            var display=new DisplayInfo(1920,1080,1,1920,1040,false);
            using(var model=new MainViewModel(new ControlledProbe(display),display,favoritesStore:store))
            {
                model.NavigateCommand.Execute("favorites");
                Require(model.IsFavorites && model.ShowFavoritesEmpty && !model.ShowScreenLens,"Favorites empty state was missing");
                model.Gaming.ToggleFavoriteCommand.Execute(null);
                Require(model.HasFavorites && model.ShowScreenLens && !model.ShowFavoritesEmpty && !model.Gaming.IsActive,
                    "Starring a tool did not show it in favorites, or started capture");
            }
            using(var reopened=new MainViewModel(new ControlledProbe(display),display,favoritesStore:store))
            {
                reopened.NavigateCommand.Execute("favorites");
                Require(reopened.Gaming.IsFavorite && reopened.ShowScreenLens,"Favorites did not survive restart");
                reopened.Gaming.ToggleFavoriteCommand.Execute(null);
                Require(reopened.ShowFavoritesEmpty,"Removing the last visible favorite did not show the empty state");
            }
            Require(store.Load().SetEquals(["future-tool"]),"Removing this tool changed other saved favorites");
            File.WriteAllText(path,"{");
            Require(store.Load().Count==0,"Broken favorites prevented an empty usable view");
            Require(!store.Save(["../invalid"]),"Invalid tool identifiers were persisted");
            Console.WriteLine("PASS Daily/favorites navigation, star/unstar, saved favorites and independent tool state");
        }
        finally
        {
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-favorites-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }

    private static void CheckShortcutPersistence()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-shortcut-tests-");
        var path=Path.Combine(directory.FullName,"gaming.json");
        try
        {
            var store=new GamingPreferencesStore(path);
            Require(store.Load().MovementMode == LensMovementMode.Fixed,"New settings did not default to fixed");
            using(var gaming=new GamingViewModel(store))
            {
                var custom=new KeyboardShortcut(3,0x5A,"Ctrl + Alt + Z");
                Require(gaming.SetShortcut(custom),"A custom shortcut was rejected");
                gaming.ActivationMode=LensActivationMode.Hold;
                gaming.IsFixedMode=true;
                gaming.WheelZoomEnabled=false;
            }
            using(var reopened=new GamingViewModel(store))
            {
                Require(reopened.ToggleShortcut?.Name == "Ctrl + Alt + Z" && reopened.IsHoldMode && reopened.IsFixedMode && !reopened.WheelZoomEnabled,
                    "Shortcut, activation or movement mode was not restored");
                reopened.FrameWidth=800; reopened.FrameHeight=600; reopened.Zoom=7;
                reopened.AdjustZoom(120);
                Require(reopened.Zoom == 7,"Disabled wheel zoom changed the multiplier");
                reopened.Zoom=10;
                Require(reopened.Zoom == 10,"The slider path could not reach 10x with wheel zoom off");
                reopened.Sharpening=1; reopened.FrameRate=144; reopened.IsFavorite=true;
                var revision=reopened.PositionResetVersion;
                reopened.ResetDefaultsCommand.Execute(null);
                Require(reopened.FrameWidth == 640 && reopened.FrameHeight == 384 && reopened.Zoom == 2 &&
                    reopened.Sharpening == 0.35 && reopened.FrameRate == 0 && reopened.IsFixedMode && reopened.WheelZoomEnabled &&
                    reopened.PositionResetVersion > revision && !reopened.IsActive && !reopened.HasCaptureResources,
                    "Restoring defaults did not reset picture, placement and resource state");
                Require(reopened.ToggleShortcut?.Name == "Ctrl + Alt + Z" && reopened.IsHoldMode && reopened.IsFavorite,
                    "Restoring defaults overwrote the user's shortcut, activation mode or favorite");
                Require(reopened.SetShortcut(null),"Empty shortcut was rejected");
            }
            using(var empty=new GamingViewModel(store))
            {
                Require(empty.ToggleShortcut is null && empty.IsHoldMode,"Cleared shortcut or mode reverted after restart");
                empty.ResetDefaults();
                Require(empty.ToggleShortcut is null && empty.IsHoldMode,"Reset restored a key the user had cleared");
                empty.IsToggleMode=true;
            }
            Require(store.Load().ActivationMode == LensActivationMode.Toggle,"Toggle mode was not persisted");
            File.WriteAllText(path,"{\"ToggleShortcut\":null,\"CloseShortcut\":{\"Modifiers\":6,\"VirtualKey\":120,\"Name\":\"Ctrl + Shift + F9\"}}");
            Require(store.Load() is { ToggleShortcut:null,ActivationMode:LensActivationMode.Toggle,MovementMode:LensMovementMode.Fixed,WheelZoomEnabled:true },
                "Legacy empty shortcut or the new fixed default was not preserved");
            File.WriteAllText(path,"{\"ToggleShortcut\":{\"Modifiers\":3,\"VirtualKey\":90,\"Name\":\"Ctrl + Alt + Z\"},\"CloseShortcut\":null}");
            Require(store.Load().ToggleShortcut?.Name == "Ctrl + Alt + Z","Legacy custom shortcut was not preserved");
            File.WriteAllText(path,"{\"ToggleShortcut\":null,\"MovementMode\":0}");
            Require(store.Load() is { ToggleShortcut:null,MovementMode:LensMovementMode.Movable },"An explicitly saved movable choice was overwritten");
            File.WriteAllText(path,"{");
            Require(store.Load().IsValid,"Corrupt preferences prevented a usable default");
            Console.WriteLine("PASS Shortcut and movement choices persist; restoring defaults preserves custom/empty shortcuts, activation mode and favorites");
        }
        finally
        {
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-shortcut-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
    }
}
