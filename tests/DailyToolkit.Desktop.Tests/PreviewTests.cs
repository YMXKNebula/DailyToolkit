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
            var colored=Pixels(renderer.Render(settings with { BorderColor=0x123456 }).Magnified);
            Require(colored[0] == 0x56 && colored[1] == 0x34 && colored[2] == 0x12,"The selected RGB color did not reach the GPU border");
            for (var y=2;y<192-2;y++)
                Require(original.AsSpan((y*320+2)*4,(320-4)*4).SequenceEqual(colored.AsSpan((y*320+2)*4,(320-4)*4)),
                    "Changing border color altered the magnified picture");
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
            foreach(var (pointerX,pointerY) in new[] { (0.9375,0.8),(0.0625,0.2) })
            {
                var atEdge=settings with { Width=320,Height=192,PointerX=pointerX,PointerY=pointerY };
                renderer.Render(atEdge);
                var enlarged=renderer.Render(atEdge with { Width=640,Height=768 });
                var bounds=enlarged.Layout.Output;
                var aligned=atEdge with
                {
                    Width=640,Height=768,
                    PointerX=(bounds.Left+bounds.Width/2)/(double)atEdge.ScreenWidth,
                    PointerY=(bounds.Top+bounds.Height/2)/(double)atEdge.ScreenHeight
                };
                var expected=renderer.Render(aligned);
                Require(enlarged.Layout.Source == expected.Layout.Source &&
                    Pixels(enlarged.Magnified).SequenceEqual(Pixels(expected.Magnified)),
                    "Resizing an edge preview did not align its real shader output with the displayed frame");
            }
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
            gamingPreferencesStore:new GamingPreferencesStore(Path.Combine(directory.FullName,"gaming.json")),
            favoritesStore:new FavoritesStore(Path.Combine(directory.FullName,"favorites.json")),
            navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json")));
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
            var picker=(DailyToolkit.Desktop.Controls.LensColorPicker)window.FindName("LensFrameColor");
            IEnumerable<System.Windows.Controls.Button> Buttons(DependencyObject root)
            {
                for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
                {
                    var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);
                    if (child is System.Windows.Controls.Button button) yield return button;
                    foreach(var descendant in Buttons(child)) yield return descendant;
                }
            }
            Buttons(picker).Single(button => button.Tag as string == "#4285F4").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(model.Gaming.BorderColor == "#4285F4","Clicking a color swatch did not change settings");
            var timeout=DateTime.UtcNow.AddSeconds(5);
            while(preview.CurrentFrame?.Settings.BorderColor != 0x4285F4 && DateTime.UtcNow < timeout) await Task.Delay(20);
            Require(preview.CurrentFrame?.Settings.BorderColor == 0x4285F4,"The selected swatch did not refresh the photo preview");
            model.Gaming.IsFavorite=true;
            var favoriteButton=(System.Windows.Controls.Button)window.FindName("FavoritesButton");
            favoriteButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var transition=(System.Windows.Controls.Image)window.FindName("SidebarTransition");
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                Require(transition.IsVisible && transition.Clip is System.Windows.Media.GeometryGroup group &&
                    group.Children[1] is System.Windows.Media.StreamGeometry star && star.Transform is System.Windows.Media.TransformGroup transforms &&
                    transforms.Children[0].HasAnimatedProperties && transforms.Children[1].HasAnimatedProperties,
                    "Favorites did not animate a growing and rotating star from the sidebar");
                await Task.Delay(600);
                Require(!transition.IsVisible && transition.Source is null && ((UIElement)window.FindName("SidebarBase")).IsHitTestVisible,
                    "The transition retained its snapshot or blocked sidebar input");
            }
            Require(model.IsFavorites && model.NavigationItems.Single().Id == "screen-lens" && !model.Gaming.IsActive,
                "The animated favorites button did not replace the sidebar");
            Buttons(window).Single(button => button.Content as string == "← 退出收藏夹").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                Require(transition.IsVisible && transition.Clip is System.Windows.Media.StreamGeometry,"Exiting favorites did not reverse the star reveal");
                // A quick second entry must not be cleared by the previous animation's completion.
                await Task.Delay(180);
                favoriteButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Task.Delay(140);
                Require(model.IsFavorites && transition.IsVisible,"An older transition cleared a newer one");
                await Task.Delay(400);
                Require(!transition.IsVisible && transition.Source is null,"Rapid transitions leaked their snapshot");
                model.ExitFavoritesCommand.Execute(null);
            }
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
