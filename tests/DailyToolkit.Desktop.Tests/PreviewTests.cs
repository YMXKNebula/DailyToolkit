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
            navigationStore:new NavigationOrderStore(Path.Combine(directory.FullName,"navigation.json")),
            appPreferencesStore:new AppPreferencesStore(Path.Combine(directory.FullName,"settings.json")));
        model.Page="gaming";
        var window=new MainWindow(model,enableShortcuts:false)
        {
            ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000
        };
        try
        {
            window.Show();
            var preview=(DailyToolkit.Desktop.Controls.LensPreview)window.FindName("LensPhotoPreview");
            await window.WaitForLensPreviewAsync().WaitAsync(TimeSpan.FromSeconds(15));
            window.UpdateLayout();
            Require(((UIElement)window.FindName("LensDetailsPanel")).IsVisible && preview.IsVisible &&
                window.FindName("LensDetailsToggle") is null && !model.Gaming.IsActive,
                "Tool navigation did not show every setting and photo without a dropdown, or started capture");
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
            var transition=(System.Windows.Controls.Image)window.FindName("PageTransition");
            var tint=(System.Windows.Shapes.Path)window.FindName("PageTransitionShape");
            var cover=(FrameworkElement)window.FindName("PageTransitionCover");
            var root=(FrameworkElement)window.PreviewContent;
            var body=(FrameworkElement)window.FindName("BodyContent");
            var settingsButton=(System.Windows.Controls.Button)window.FindName("SettingsButton");
            var caption=(FrameworkElement)window.FindName("CaptionPanel");
            void RequireCovered(bool gear)
            {
                window.UpdateLayout();
                var corners=new[] { new Point(2,2),new Point(body.ActualWidth-2,2),new Point(2,body.ActualHeight-2),new Point(body.ActualWidth-2,body.ActualHeight-2) };
                var inverse=tint.RenderTransform.Inverse;
                Require(cover.Opacity > 0.999 && tint.Data is { } shape && inverse is not null &&
                    corners.All(point => shape.FillContains(inverse.Transform(point))),
                    "The star or gear faded before fully covering every content corner");
                Require(caption.ActualHeight == 36 && System.Windows.Shell.WindowChrome.GetWindowChrome(window) is not null &&
                    System.Windows.Media.VisualTreeHelper.GetParent(cover) == body && System.Windows.Controls.Grid.GetRow(body) == 1,
                    "The transition covered the title bar");
                var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(root);
                var pixels=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX),
                    (int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,System.Windows.Media.PixelFormats.Pbgra32);
                pixels.Render(root);
                foreach (var bodyPoint in corners.Append(new Point(body.ActualWidth/2,body.ActualHeight/2)))
                {
                    var point=body.TranslatePoint(bodyPoint,root);
                    var expected=((System.Windows.Media.SolidColorBrush)window.FindResource("AnimationBrush")).Color;
                    var rgba=new byte[4];
                    pixels.CopyPixels(new Int32Rect((int)(point.X*dpi.DpiScaleX),(int)(point.Y*dpi.DpiScaleY),1,1),rgba,4,0);
                    Require(rgba[3] == 255 && Math.Abs(rgba[0]-expected.B)<=3 && Math.Abs(rgba[1]-expected.G)<=3 && Math.Abs(rgba[2]-expected.R)<=3,
                        "The displayed transition did not uniformly cover the main content and sidebar");
                }
                var captionColor=((System.Windows.Media.SolidColorBrush)window.FindResource("SurfaceBrush")).Color;
                var titlePixel=new byte[4];
                pixels.CopyPixels(new Int32Rect((int)(root.ActualWidth/2*dpi.DpiScaleX),(int)(18*dpi.DpiScaleY),1,1),titlePixel,4,0);
                Require(titlePixel[0] == captionColor.B && titlePixel[1] == captionColor.G && titlePixel[2] == captionColor.R,
                    "An animation painted over the title bar");
            }
            void RequireDirection(bool opening)
            {
                var silhouette=transition.Clip is System.Windows.Media.GeometryGroup outside
                    ? outside.Children[1] : transition.Clip;
                Require(silhouette is System.Windows.Media.StreamGeometry &&
                    silhouette.Transform == tint.RenderTransform && tint.Data is { IsFrozen:true },
                    "The snapshot mask and immutable filled outline did not share their render transform");
                var transforms=(System.Windows.Media.TransformGroup)silhouette!.Transform;
                var scale=(System.Windows.Media.ScaleTransform)transforms.Children[0];
                var rotation=(System.Windows.Media.RotateTransform)transforms.Children[1];
                var delay=opening ? 0 : 140;
                window.SeekPageTransition(TimeSpan.FromMilliseconds(delay+64));
                var firstScale=scale.ScaleX; var firstAngle=rotation.Angle;
                window.SeekPageTransition(TimeSpan.FromMilliseconds(delay+256));
                Require(opening ? scale.ScaleX > firstScale && rotation.Angle > firstAngle
                    : scale.ScaleX < firstScale && rotation.Angle < firstAngle,
                    opening ? "The opening shape did not expand and rotate outwards" : "The closing shape did not retract and reverse its rotation");
                // The outgoing page stays outside an expanding mask, but inside a retracting one.
                if (!opening)
                    Require(transition.Clip is System.Windows.Media.StreamGeometry &&
                        !transition.Clip.FillContains(new Point(body.ActualWidth-2,2)),
                        "The closing snapshot covered the already revealed destination outside the shape");
                Require(cover.Opacity > .999,"The motion faded its fill before it finished");
            }
            Require(model.FavoritesToggleHint == "关闭收藏夹" && favoriteButton.ToolTip as string == "关闭收藏夹",
                "The same star did not advertise its close action");
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                await Application.Current.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.Render);
                window.SeekPageTransition(TimeSpan.FromMilliseconds(120));
                Require(transition.IsVisible && transition.Clip is System.Windows.Media.GeometryGroup group &&
                    group.Children[1] is System.Windows.Media.StreamGeometry star && star.Transform is System.Windows.Media.TransformGroup transforms &&
                    transforms.Children[0].HasAnimatedProperties && transforms.Children[1].HasAnimatedProperties,
                    $"Favorites did not animate a growing and rotating star: visible={transition.IsVisible}, clip={transition.Clip?.GetType().Name}, transform={tint.Data?.Transform}, enabled={model.PageAnimationsEnabled}");
                var geometry=(System.Windows.Media.StreamGeometry)((System.Windows.Media.GeometryGroup)transition.Clip!).Children[1];
                var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(root);
                Require(System.Windows.Controls.Grid.GetColumnSpan(transition) == 2 && System.Windows.Controls.Grid.GetColumnSpan(cover) == 2 &&
                    transition.Source is System.Windows.Media.Imaging.BitmapSource snapshot &&
                    snapshot.PixelWidth == (int)Math.Ceiling(body.ActualWidth*dpi.DpiScaleX) &&
                    snapshot.PixelHeight == (int)Math.Ceiling(body.ActualHeight*dpi.DpiScaleY) && tint.RenderTransform == geometry.Transform && tint.IsVisible &&
                    favoriteButton.IsHitTestVisible && !((UIElement)window.FindName("MainContent")).IsHitTestVisible,
                    "The star transition did not cover both sidebar and main content, or blocked the toggle itself");
                RequireDirection(true);
                window.SeekPageTransition(TimeSpan.FromMilliseconds(320));
                RequireCovered(false);
                await Task.Delay(260);
                Require(!transition.IsVisible && transition.Source is null && !tint.IsVisible && tint.Data is null &&
                    ((UIElement)window.FindName("MainContent")).IsHitTestVisible && ((UIElement)window.FindName("NavigationPanel")).IsHitTestVisible,
                    "The transition retained its snapshot or blocked window input");
            }
            Require(model.IsFavorites && model.NavigationItems.Single().Id == "screen-lens" && !model.Gaming.IsActive,
                "The animated favorites button did not replace the sidebar");
            favoriteButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(!model.IsFavorites && model.ShowScreenLens && model.FavoritesToggleHint == "打开收藏夹",
                "Clicking the same star did not close favorites and restore the previous tool");
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                window.SeekPageTransition(TimeSpan.Zero);
                Require(transition.IsVisible && transition.Clip is System.Windows.Media.StreamGeometry && cover.Opacity < .001,
                    "Closing favorites did not preserve the outgoing page before retracting it");
                window.SeekPageTransition(TimeSpan.FromMilliseconds(140)); RequireCovered(false);
                RequireDirection(false);
                window.SeekPageTransition(TimeSpan.Zero);
                // A quick second entry must not be cleared by the previous animation's completion.
                await Task.Delay(180);
                favoriteButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Task.Delay(140);
                Require(model.IsFavorites && transition.IsVisible,"An older transition cleared a newer one");
                await Task.Delay(400);
                Require(!transition.IsVisible && transition.Source is null,"Rapid transitions leaked their snapshot");
                model.ExitFavoritesCommand.Execute(null);
            }
            settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(model.IsSettings && !model.IsFavorites && model.PageTitle == "主题配色" &&
                ((UIElement)window.FindName("SettingsScroll")).IsVisible && settingsButton.ToolTip as string == "返回工具" &&
                settingsButton.TranslatePoint(new Point(),root).X > favoriteButton.TranslatePoint(new Point(),root).X,
                "The gear did not sit beside the star or open software settings");
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                Require(transition.IsVisible && tint.Data is System.Windows.Media.StreamGeometry,
                    "Settings reused the star instead of a filled gear");
                window.SeekPageTransition(TimeSpan.FromMilliseconds(320));
                Require(tint.Data!.FillContains(tint.RenderTransform.Inverse!.Transform(
                    settingsButton.TranslatePoint(new Point(settingsButton.ActualWidth/2,settingsButton.ActualHeight/2),body))),
                    "The gear had an unfilled center");
                RequireCovered(true);
                RequireDirection(true);
                await Task.Delay(260);
            }
            settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(!model.IsSettings && model.ShowScreenLens,"Clicking the same gear did not return to the selected tool");
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                RequireDirection(false);
                window.SeekPageTransition(TimeSpan.FromMilliseconds(140));
                RequireCovered(true);
                await Task.Delay(400);
                settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Task.Delay(120);
                favoriteButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await Task.Delay(360);
                Require(model.IsFavorites && transition.IsVisible,"An older gear animation cleared a newer star animation");
                await Task.Delay(220);
                Require(transition.Source is null && !cover.IsVisible && tint.Data is null,"Rapid star and gear transitions retained resources");
                model.ExitFavoritesCommand.Execute(null);
                settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                window.Width=720; window.UpdateLayout();
                Require(transition.Source is null && !cover.IsVisible && ((UIElement)window.FindName("MainContent")).IsHitTestVisible,
                    "Resizing during an animation retained an undersized cover or blocked input");
            }
            if (!model.IsSettings) settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var animationSwitch=(System.Windows.Controls.CheckBox)window.FindName("PageAnimationSwitch");
            var starIcon=(System.Windows.Shapes.Path)window.FindName("FavoritesIcon");
            var gearIcon=(System.Windows.Shapes.Path)window.FindName("SettingsIcon");
            Require(Math.Abs(starIcon.ActualWidth-gearIcon.ActualWidth) < .01 && Math.Abs(starIcon.ActualHeight-gearIcon.ActualHeight) < .01 &&
                starIcon.ActualWidth is > 25 and < 27 && !gearIcon.Data.FillContains(new Point()),
                $"The footer icons had unequal sizes or the small gear was filled: star={starIcon.ActualWidth}x{starIcon.ActualHeight}, gear={gearIcon.ActualWidth}x{gearIcon.ActualHeight}");
            model.AnimationColor="#4285F4";
            model.DarkThemeCommand.Execute(null); window.UpdateLayout();
            var themePreview=(DailyToolkit.Desktop.Controls.ThemePreview)window.FindName("PalettePreview");
            Require(themePreview.Palette == ThemePalette.Dark && themePreview.AnimationColor == "#4285F4" &&
                ((System.Windows.Media.SolidColorBrush)window.FindResource("WindowBrush")).Color.ToString() == "#FF171D1B",
                "The live theme and preview did not receive the selected palette");
            model.LightThemeCommand.Execute(null);
            var settingsCards=new[] { "ThemeSettingsCard","AnimationSettingsCard","BackgroundSettingsCard","StartupSettingsCard","AboutSettingsCard" };
            foreach (var (item,index) in model.NavigationItems.ToArray().Select((item,index) => (item,index)))
            {
                model.NavigateCommand.Execute(item); window.UpdateLayout();
                Require(model.IsSettings && item.IsSelected && model.PageTitle == item.Name &&
                    settingsCards.Count(name => ((UIElement)window.FindName(name)).IsVisible) == 1 &&
                    ((UIElement)window.FindName(settingsCards[index])).IsVisible,
                    "Settings navigation did not select exactly one category page");
            }
            Require(!Buttons(window).Any(button => button.Content as string == "应用启动设置"),
                "The removed startup apply button was still present");
            model.Page="settings-animation"; window.UpdateLayout();
            var animationPicker=(DailyToolkit.Desktop.Controls.ColorPicker)window.FindName("AnimationColorPicker");
            var customButton=(System.Windows.Controls.Button)animationPicker.FindName("CustomColorButton");
            Require(customButton.Content is DailyToolkit.Desktop.Controls.ColorWheelIcon,"Custom colors still used a monochrome glyph");
            var wheel=(FrameworkElement)customButton.Content;
            var wheelDpi=System.Windows.Media.VisualTreeHelper.GetDpi(wheel);
            var wheelBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(wheel.ActualWidth*wheelDpi.DpiScaleX),
                (int)Math.Ceiling(wheel.ActualHeight*wheelDpi.DpiScaleY),wheelDpi.PixelsPerInchX,wheelDpi.PixelsPerInchY,System.Windows.Media.PixelFormats.Pbgra32);
            wheelBitmap.Render(wheel);
            var wheelColors=new HashSet<int>();
            for (var segment=0;segment<6;segment++)
            {
                var angle=(-60+segment*60)*Math.PI/180;
                var rgba=new byte[4];
                wheelBitmap.CopyPixels(new Int32Rect((int)(wheelBitmap.PixelWidth*(.5+.30*Math.Cos(angle))),
                    (int)(wheelBitmap.PixelHeight*(.5+.30*Math.Sin(angle))),1,1),rgba,4,0);
                Require(rgba[3] > 200 && rgba.Take(3).Max()-rgba.Take(3).Min() > 35,"The custom color icon was obscured or lost its colors");
                wheelColors.Add(rgba[0] | rgba[1]<<8 | rgba[2]<<16);
            }
            Require(wheelColors.Count == 6,"The custom color icon did not display all six colors");
            Require(Buttons(picker).Any(button => button.Name == "CustomColorButton") &&
                ((System.Windows.Controls.StackPanel)Buttons(picker).Single(button => button.Name == "CustomColorButton").Content)
                    .Children.OfType<DailyToolkit.Desktop.Controls.ColorWheelIcon>().Any(),
                "The lens color picker did not reuse the same color icon");
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                window.SeekPageTransition(TimeSpan.FromMilliseconds(140)); RequireCovered(true); RequireDirection(false); await Task.Delay(260);
                settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                window.SeekPageTransition(TimeSpan.FromMilliseconds(320)); RequireCovered(true); RequireDirection(true); await Task.Delay(260);
            }
            animationSwitch.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
            Require(!model.PageAnimationsEnabled && new AppPreferencesStore(Path.Combine(directory.FullName,"settings.json")).Load().PageAnimationsEnabled == false,
                "The actual animation checkbox did not persist the choice");
            settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Require(!model.IsSettings && !transition.IsVisible && transition.Source is null,"Disabling animation still allocated a transition snapshot");
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
        Console.WriteLine("PASS Star/gear transitions expand on opening, retract on closing and share an immutable render outline; caption, rapid changes and cleanup stay correct");
    }
}
