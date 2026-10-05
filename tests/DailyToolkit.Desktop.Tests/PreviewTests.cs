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
    private static async Task CheckPhotoPreviewAsync()
    {
        await Task.Run(() =>
        {
            using var renderer=new LensPhotoPreviewRenderer(software:true);
            var settings=new LensPreviewSettings(2560,1440,320,192,2,0,0);
            var first=renderer.Render(settings);
            Require(first.Photo.IsFrozen && first.Magnified.IsFrozen && first.Photo.PixelWidth == 1920,"Local photo was not loaded safely for background rendering");
            byte[] Pixels(System.Windows.Media.Imaging.BitmapSource bitmap)
            {
                var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];
                bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
                return pixels;
            }
            var original=Pixels(first.Magnified);
            var guided=Pixels(renderer.Render(settings with { VerticalGuide=true,HorizontalGuide=true }).Magnified);
            Require(guided.SequenceEqual(original),"Desktop guides were painted inside the magnified photo");
            Require(Pixels(renderer.Render(settings).Magnified).SequenceEqual(original),"Ending assistance left guide pixels in the output");
            var sharp=Pixels(renderer.Render(settings with { Sharpening=1 }).Magnified);
            Require(!original.SequenceEqual(sharp),"Sharpening did not change the actual photo shader output");
            var zoom=renderer.Render(settings with { Zoom=10 });
            Require(!original.SequenceEqual(Pixels(zoom.Magnified)),"Zoom did not change the crop");
            var resized=renderer.Render(settings with { Width=480,Height=640,PointerX=1,PointerY=1 });
            Require(resized.Magnified.PixelWidth == 480 && resized.Magnified.PixelHeight == 640 &&
                resized.Layout.Output == new PixelBounds(2080,800,480,640),"Independent size or edge-constrained dragging was incorrect");
            var clamped=renderer.Render(settings with { ScreenWidth=200,ScreenHeight=100,Width=480,Height=640,Zoom=1 });
            Require(clamped.Magnified.PixelWidth == 200 && clamped.Magnified.PixelHeight == 100,"Preview did not match monitor size limits");
        });
        Console.WriteLine("PASS Bundled photo preview runs the real shader, with zoom, sharpening, independent dimensions and monitor clipping");
    }

    private static async Task CheckPreviewLayoutAsync()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-lens-layout-tests-");
        var display=new DisplayInfo(1920,1080,1,1920,1040,false);
        var probe=new ControlledProbe(display);
        probe.Finish.TrySetResult();
        var model=new MainViewModel(probe,display,new LocalProbe(),
            gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")));
        model.Page="gaming";
        var window=new MainWindow(model,enableShortcuts:false)
        {
            ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000
        };
        try
        {
            window.Show();
            var preview=(DailyToolkit.Desktop.Controls.LensPreview)window.FindName("LensPhotoPreview");
            Require(preview.CurrentFrame is null && !model.Gaming.IsActive,"Collapsed tool decoded the photo or started capture");
            window.ShowPreviewDetails(false);
            await window.WaitForLensPreviewAsync().WaitAsync(TimeSpan.FromSeconds(15));
            window.UpdateLayout();
            var fixedRadio=(System.Windows.Controls.RadioButton)window.FindName("LensFixedMode");
            var movableRadio=(System.Windows.Controls.RadioButton)window.FindName("LensMovableMode");
            Require(fixedRadio.IsChecked == true && movableRadio.IsChecked == false,"Position radio buttons did not default to fixed");
            movableRadio.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            Require(model.Gaming.IsMovableMode && fixedRadio.IsChecked == false,"Choosing movable in the actual settings did not update the lens mode");
            fixedRadio.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            Require(model.Gaming.IsFixedMode && movableRadio.IsChecked == false,"Choosing fixed in the actual settings did not lock the lens mode");
            var wheelBox=(System.Windows.Controls.CheckBox)window.FindName("LensWheelZoom");
            wheelBox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
            Require(!model.Gaming.WheelZoomEnabled,"The settings wheel switch did not update the tool");
            wheelBox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
            Require(model.Gaming.WheelZoomEnabled,"The settings wheel switch did not reenable zoom");
            Require(preview.CurrentFrame is not null && !model.Gaming.IsActive && System.Windows.Controls.Grid.GetColumn(preview) == 1,
                "Expanded preview was missing or did not sit beside settings");
            window.Width=680;
            window.UpdateLayout();
            Require(System.Windows.Controls.Grid.GetRow(preview) == 1 && System.Windows.Controls.Grid.GetColumn(preview) == 0,
                "Narrow layout did not place the preview below the settings");
        }
        finally
        {
            window.Close(); await model.PendingWork;
            var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (Path.GetFullPath(directory.FullName).StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) &&
                directory.Name.StartsWith("DailyToolkit-lens-layout-tests-",StringComparison.Ordinal)) directory.Delete(recursive:true);
        }
        Console.WriteLine("PASS Preview loads only when expanded, stays offline, leaves capture off and adapts to narrow windows");
    }
}
